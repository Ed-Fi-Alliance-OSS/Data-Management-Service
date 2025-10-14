// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    [TestFixture]
    [Parallelizable]
    public class CoerceDateFormatMiddlewareTests
    {
        internal static IPipelineStep Middleware()
        {
            return new CoerceDateFormatMiddleware(NullLogger.Instance);
        }

        internal static ApiSchemaDocuments SchemaDocuments()
        {
            var builder = new JsonSchemaBuilder();
            builder.Title("Ed-Fi.AcademicWeek");
            builder.Description("This entity represents an academic week");
            builder.Schema("https://json-schema.org/draft/2020-12/schema");
            builder.AdditionalProperties(false);
            builder
                .Properties(
                    ("weekIdentifier", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                    ("beginDate", new JsonSchemaBuilder().Type(SchemaValueType.String).Format("date")),
                    ("endDate", new JsonSchemaBuilder().Type(SchemaValueType.String).Format("date")),
                    (
                        "events",
                        new JsonSchemaBuilder()
                            .Type(SchemaValueType.Array)
                            .Items(
                                new JsonSchemaBuilder()
                                    .Type(SchemaValueType.Object)
                                    .Properties(
                                        (
                                            "eventDate",
                                            new JsonSchemaBuilder()
                                                .Type(SchemaValueType.String)
                                                .Format("date-time")
                                        )
                                    )
                                    .Required("eventDate")
                            )
                    )
                )
                .Required("weekIdentifier", "beginDate", "endDate");

            return new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("AcademicWeek")
                .WithJsonSchemaForInsert(builder.Build()!)
                .WithDateJsonPaths(new[] { "$.beginDate", "$.endDate", "$.events[*].eventDate" })
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        private static readonly Func<Task> _next = TestHelper.NullNext;

        [TestFixture]
        [Parallelizable]
        public class Given_A_Request_With_Slash_Formatted_Dates : CoerceDateFormatMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                string requestBody = """
                    {
                        "weekIdentifier": "Week1",
                        "beginDate": "5/1/2009",
                        "endDate": "5/7/2009",
                        "events": [
                            {
                                "eventDate": "5/3/2009"
                            },
                            {
                                "eventDate": "5/5/2009"
                            }
                        ]
                    }
                    """;

                var frontEndRequest = new FrontendRequest(
                    "ed-fi/academicWeeks",
                    Body: requestBody,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
                );

                _requestInfo = new(frontEndRequest, RequestMethod.POST)
                {
                    ApiSchemaDocuments = SchemaDocuments(),
                    ParsedBody = JsonNode.Parse(requestBody)!,
                    PathComponents = new(
                        ProjectEndpointName: new("ed-fi"),
                        EndpointName: new("academicWeeks"),
                        DocumentUuid: No.DocumentUuid
                    ),
                };

                _requestInfo.ProjectSchema =
                    _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
                _requestInfo.ResourceSchema = new ResourceSchema(
                    _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                        ?? new JsonObject()
                );

                await Middleware().Execute(_requestInfo, _next);
            }

            [Test]
            public void Should_Convert_Top_Level_Date_Fields_To_ISO8601_Format()
            {
                // Verify beginDate was converted from "5/1/2009" to "2009-05-01"
                var beginDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.beginDate",
                    NullLogger.Instance
                );
                beginDate!.GetValue<string>().Should().Be("2009-05-01");

                // Verify endDate was converted from "5/7/2009" to "2009-05-07"
                var endDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.endDate",
                    NullLogger.Instance
                );
                endDate!.GetValue<string>().Should().Be("2009-05-07");
            }

            [Test]
            public void Should_Convert_Array_Date_Fields_To_ISO8601_Format()
            {
                // Verify array event dates were converted
                var firstEventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.events[0].eventDate",
                    NullLogger.Instance
                );
                firstEventDate!.GetValue<string>().Should().Be("2009-05-03");

                var secondEventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.events[1].eventDate",
                    NullLogger.Instance
                );
                secondEventDate!.GetValue<string>().Should().Be("2009-05-05");
            }

            [Test]
            public void Should_Not_Modify_Non_Date_Fields()
            {
                // Verify non-date fields remain unchanged
                var weekIdentifier = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.weekIdentifier",
                    NullLogger.Instance
                );
                weekIdentifier!.GetValue<string>().Should().Be("Week1");
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Request_With_Mixed_Date_Formats : CoerceDateFormatMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                string requestBody = """
                    {
                        "weekIdentifier": "Week2",
                        "beginDate": "2009-05-01",
                        "endDate": "5/7/2009",
                        "events": [
                            {
                                "eventDate": "2009-05-03"
                            }
                        ]
                    }
                    """;

                var frontEndRequest = new FrontendRequest(
                    "ed-fi/academicWeeks",
                    Body: requestBody,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
                );

                _requestInfo = new(frontEndRequest, RequestMethod.POST)
                {
                    ApiSchemaDocuments = SchemaDocuments(),
                    ParsedBody = JsonNode.Parse(requestBody)!,
                    PathComponents = new(
                        ProjectEndpointName: new("ed-fi"),
                        EndpointName: new("academicWeeks"),
                        DocumentUuid: No.DocumentUuid
                    ),
                };

                _requestInfo.ProjectSchema =
                    _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
                _requestInfo.ResourceSchema = new ResourceSchema(
                    _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                        ?? new JsonObject()
                );

                await Middleware().Execute(_requestInfo, _next);
            }

            [Test]
            public void Should_Convert_Only_Slash_Formatted_Dates()
            {
                // ISO-8601 format should remain unchanged
                var beginDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.beginDate",
                    NullLogger.Instance
                );
                beginDate!.GetValue<string>().Should().Be("2009-05-01");

                // Slash format should be converted
                var endDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.endDate",
                    NullLogger.Instance
                );
                endDate!.GetValue<string>().Should().Be("2009-05-07");

                // ISO-8601 in array should remain unchanged
                var eventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.events[0].eventDate",
                    NullLogger.Instance
                );
                eventDate!.GetValue<string>().Should().Be("2009-05-03");
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Request_With_Various_Slash_Date_Formats : CoerceDateFormatMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                string requestBody = """
                    {
                        "weekIdentifier": "Week3",
                        "beginDate": "05/01/2009",
                        "endDate": "5/7/09",
                        "events": [
                            {
                                "eventDate": "1/10/2009"
                            }
                        ]
                    }
                    """;

                var frontEndRequest = new FrontendRequest(
                    "ed-fi/academicWeeks",
                    Body: requestBody,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
                );

                _requestInfo = new(frontEndRequest, RequestMethod.POST)
                {
                    ApiSchemaDocuments = SchemaDocuments(),
                    ParsedBody = JsonNode.Parse(requestBody)!,
                    PathComponents = new(
                        ProjectEndpointName: new("ed-fi"),
                        EndpointName: new("academicWeeks"),
                        DocumentUuid: No.DocumentUuid
                    ),
                };

                _requestInfo.ProjectSchema =
                    _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
                _requestInfo.ResourceSchema = new ResourceSchema(
                    _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                        ?? new JsonObject()
                );

                await Middleware().Execute(_requestInfo, _next);
            }

            [Test]
            public void Should_Convert_Different_Slash_Date_Formats()
            {
                // MM/dd/yyyy format
                var beginDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.beginDate",
                    NullLogger.Instance
                );
                beginDate!.GetValue<string>().Should().Be("2009-05-01");

                // M/d/yy format - assuming 09 means 2009
                var endDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.endDate",
                    NullLogger.Instance
                );
                endDate!.GetValue<string>().Should().Be("2009-05-07");

                // M/dd/yyyy format
                var eventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.events[0].eventDate",
                    NullLogger.Instance
                );
                eventDate!.GetValue<string>().Should().Be("2009-01-10");
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Request_With_Invalid_Date_Values : CoerceDateFormatMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                string requestBody = """
                    {
                        "weekIdentifier": "Week4",
                        "beginDate": "invalid/date/format",
                        "endDate": "13/45/2009",
                        "events": [
                            {
                                "eventDate": "not-a-date"
                            }
                        ]
                    }
                    """;

                var frontEndRequest = new FrontendRequest(
                    "ed-fi/academicWeeks",
                    Body: requestBody,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
                );

                _requestInfo = new(frontEndRequest, RequestMethod.POST)
                {
                    ApiSchemaDocuments = SchemaDocuments(),
                    ParsedBody = JsonNode.Parse(requestBody)!,
                    PathComponents = new(
                        ProjectEndpointName: new("ed-fi"),
                        EndpointName: new("academicWeeks"),
                        DocumentUuid: No.DocumentUuid
                    ),
                };

                _requestInfo.ProjectSchema =
                    _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
                _requestInfo.ResourceSchema = new ResourceSchema(
                    _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                        ?? new JsonObject()
                );

                await Middleware().Execute(_requestInfo, _next);
            }

            [Test]
            public void Should_Leave_Invalid_Date_Values_Unchanged()
            {
                // Invalid date formats should remain unchanged for downstream validation to handle
                var beginDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.beginDate",
                    NullLogger.Instance
                );
                beginDate!.GetValue<string>().Should().Be("invalid/date/format");

                var endDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.endDate",
                    NullLogger.Instance
                );
                endDate!.GetValue<string>().Should().Be("13/45/2009");

                var eventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.events[0].eventDate",
                    NullLogger.Instance
                );
                eventDate!.GetValue<string>().Should().Be("not-a-date");
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Request_With_Slash_Formatted_DateTimes : CoerceDateFormatMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                string requestBody = """
                    {
                        "weekIdentifier": "WeekDateTime1",
                        "beginDate": "5/1/2009",
                        "endDate": "5/7/2009",
                        "events": [
                            {
                                "eventDate": "5/3/2009 10:30:00 AM"
                            },
                            {
                                "eventDate": "5/5/2009 2:45:30 PM"
                            }
                        ]
                    }
                    """;

                var frontEndRequest = new FrontendRequest(
                    "ed-fi/academicWeeks",
                    Body: requestBody,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
                );

                _requestInfo = new(frontEndRequest, RequestMethod.POST)
                {
                    ApiSchemaDocuments = SchemaDocuments(),
                    ParsedBody = JsonNode.Parse(requestBody)!,
                    PathComponents = new(
                        ProjectEndpointName: new("ed-fi"),
                        EndpointName: new("academicWeeks"),
                        DocumentUuid: No.DocumentUuid
                    ),
                };

                _requestInfo.ProjectSchema =
                    _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
                _requestInfo.ResourceSchema = new ResourceSchema(
                    _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                        ?? new JsonObject()
                );

                await Middleware().Execute(_requestInfo, _next);
            }

            [Test]
            public void Should_Convert_DateTime_Date_Portion_To_Dash_Format()
            {
                // Verify datetime dates were converted, preserving time portions
                var firstEventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.events[0].eventDate",
                    NullLogger.Instance
                );
                firstEventDate!.GetValue<string>().Should().Be("2009-05-03 10:30:00 AM");

                var secondEventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.events[1].eventDate",
                    NullLogger.Instance
                );
                secondEventDate!.GetValue<string>().Should().Be("2009-05-05 2:45:30 PM");
            }

            [Test]
            public void Should_Still_Convert_Date_Only_Fields()
            {
                // Verify date-only fields still work
                var beginDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.beginDate",
                    NullLogger.Instance
                );
                beginDate!.GetValue<string>().Should().Be("2009-05-01");
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Request_With_Mixed_DateTime_Formats : CoerceDateFormatMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                string requestBody = """
                    {
                        "weekIdentifier": "WeekDateTime2",
                        "beginDate": "5/1/2009",
                        "endDate": "2009-05-07",
                        "events": [
                            {
                                "eventDate": "2009-05-03T14:30:00Z"
                            },
                            {
                                "eventDate": "5/5/2009 11:15 AM"
                            },
                            {
                                "eventDate": "05/06/09 23:59:59"
                            }
                        ]
                    }
                    """;

                var frontEndRequest = new FrontendRequest(
                    "ed-fi/academicWeeks",
                    Body: requestBody,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
                );

                _requestInfo = new(frontEndRequest, RequestMethod.POST)
                {
                    ApiSchemaDocuments = SchemaDocuments(),
                    ParsedBody = JsonNode.Parse(requestBody)!,
                    PathComponents = new(
                        ProjectEndpointName: new("ed-fi"),
                        EndpointName: new("academicWeeks"),
                        DocumentUuid: No.DocumentUuid
                    ),
                };

                _requestInfo.ProjectSchema =
                    _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
                _requestInfo.ResourceSchema = new ResourceSchema(
                    _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                        ?? new JsonObject()
                );

                await Middleware().Execute(_requestInfo, _next);
            }

            [Test]
            public void Should_Convert_Only_Slash_Formatted_DateTime_Portions()
            {
                // ISO datetime should remain unchanged
                var firstEventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.events[0].eventDate",
                    NullLogger.Instance
                );
                firstEventDate!.GetValue<string>().Should().Be("2009-05-03T14:30:00Z");

                // Slash datetime should have date portion converted
                var secondEventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.events[1].eventDate",
                    NullLogger.Instance
                );
                secondEventDate!.GetValue<string>().Should().Be("2009-05-05 11:15 AM");

                // Different slash format with time
                var thirdEventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                    "$.events[2].eventDate",
                    NullLogger.Instance
                );
                thirdEventDate!.GetValue<string>().Should().Be("2009-05-06 23:59:59");
            }
        }
    }
}
