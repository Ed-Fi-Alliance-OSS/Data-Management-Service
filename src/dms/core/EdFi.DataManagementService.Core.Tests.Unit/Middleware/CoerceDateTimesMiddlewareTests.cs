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

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class CoerceDateTimesMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new CoerceDateTimesMiddleware(NullLogger.Instance);
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
            .WithDateTimeJsonPaths(new[] { "$.beginDate", "$.endDate", "$.events[*].eventDate" })
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    private static readonly Func<Task> _next = TestHelper.NullNext;

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Various_DateTime_Formats : CoerceDateTimesMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string requestBody = """
                {
                    "weekIdentifier": "Week1",
                    "beginDate": "2009-05-01T08:30:00",
                    "endDate": "5/7/2009 2:15:30 PM",
                    "events": [
                        {
                            "eventDate": "2009-05-03T10:45:00Z"
                        },
                        {
                            "eventDate": "2009-05-05 14:30:00"
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

            _requestInfo.ProjectSchema = _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
                new("ed-fi")
            )!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
            );

            await Middleware().Execute(_requestInfo, _next);
        }

        [Test]
        public void Should_Convert_DateTime_Without_Timezone_To_UTC_ISO8601()
        {
            // beginDate should be converted to UTC format
            var beginDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.beginDate",
                NullLogger.Instance
            );
            beginDate!.GetValue<string>().Should().Be("2009-05-01T08:30:00Z");
        }

        [Test]
        public void Should_Convert_DateTime_With_AM_PM_To_UTC_ISO8601()
        {
            // endDate should be converted from "5/7/2009 2:15:30 PM" to UTC format
            var endDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.endDate",
                NullLogger.Instance
            );
            endDate!.GetValue<string>().Should().Be("2009-05-07T14:15:30Z");
        }

        [Test]
        public void Should_Keep_Already_UTC_DateTime_Format()
        {
            // First event date is already in UTC format, should remain unchanged
            var firstEventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.events[0].eventDate",
                NullLogger.Instance
            );
            firstEventDate!.GetValue<string>().Should().Be("2009-05-03T10:45:00Z");
        }

        [Test]
        public void Should_Convert_DateTime_Without_Explicit_Timezone_In_Arrays()
        {
            // Second event date should be converted to UTC format
            var secondEventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.events[1].eventDate",
                NullLogger.Instance
            );
            secondEventDate!.GetValue<string>().Should().Be("2009-05-05T14:30:00Z");
        }

        [Test]
        public void Should_Not_Modify_Non_DateTime_Fields()
        {
            // Non-datetime fields should remain unchanged
            var weekIdentifier = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.weekIdentifier",
                NullLogger.Instance
            );
            weekIdentifier!.GetValue<string>().Should().Be("Week1");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_ISO_Date_Only_Formats : CoerceDateTimesMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string requestBody = """
                {
                    "weekIdentifier": "Week2",
                    "beginDate": "2009-05-01",
                    "endDate": "2009-05-07",
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

            _requestInfo.ProjectSchema = _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
                new("ed-fi")
            )!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
            );

            await Middleware().Execute(_requestInfo, _next);
        }

        [Test]
        public void Should_Convert_Date_Only_To_Midnight_UTC()
        {
            // Date-only formats should be converted to midnight UTC
            var beginDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.beginDate",
                NullLogger.Instance
            );
            beginDate!.GetValue<string>().Should().Be("2009-05-01T00:00:00Z");

            var endDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.endDate",
                NullLogger.Instance
            );
            endDate!.GetValue<string>().Should().Be("2009-05-07T00:00:00Z");
        }

        [Test]
        public void Should_Convert_Array_Date_Only_To_Midnight_UTC()
        {
            var eventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.events[0].eventDate",
                NullLogger.Instance
            );
            eventDate!.GetValue<string>().Should().Be("2009-05-03T00:00:00Z");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Invalid_Date_Formats : CoerceDateTimesMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string requestBody = """
                {
                    "weekIdentifier": "Week3",
                    "beginDate": "invalid-date",
                    "endDate": "not-a-date",
                    "events": [
                        {
                            "eventDate": "also-invalid"
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

            _requestInfo.ProjectSchema = _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
                new("ed-fi")
            )!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
            );

            await Middleware().Execute(_requestInfo, _next);
        }

        [Test]
        public void Should_Leave_Invalid_Date_Strings_Unchanged()
        {
            // Invalid date strings should not be modified
            var beginDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.beginDate",
                NullLogger.Instance
            );
            beginDate!.GetValue<string>().Should().Be("invalid-date");

            var endDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.endDate",
                NullLogger.Instance
            );
            endDate!.GetValue<string>().Should().Be("not-a-date");
        }

        [Test]
        public void Should_Leave_Invalid_Array_Date_Strings_Unchanged()
        {
            var eventDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.events[0].eventDate",
                NullLogger.Instance
            );
            eventDate!.GetValue<string>().Should().Be("also-invalid");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_Empty_Events_Array : CoerceDateTimesMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            string requestBody = """
                {
                    "weekIdentifier": "Week4",
                    "beginDate": "2009-05-01T09:00:00",
                    "endDate": "2009-05-07T17:00:00",
                    "events": []
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

            _requestInfo.ProjectSchema = _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
                new("ed-fi")
            )!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
            );

            await Middleware().Execute(_requestInfo, _next);
        }

        [Test]
        public void Should_Process_Top_Level_Dates_When_Array_Is_Empty()
        {
            var beginDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.beginDate",
                NullLogger.Instance
            );
            beginDate!.GetValue<string>().Should().Be("2009-05-01T09:00:00Z");

            var endDate = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.endDate",
                NullLogger.Instance
            );
            endDate!.GetValue<string>().Should().Be("2009-05-07T17:00:00Z");
        }

        [Test]
        public void Should_Keep_Empty_Array_Unchanged()
        {
            var events = _requestInfo.ParsedBody.SelectRequiredNodeFromPath("$.events", NullLogger.Instance);
            events.Should().NotBeNull();
            events!.AsArray().Count.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_With_No_DateTime_Paths_In_Schema : CoerceDateTimesMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        private static ApiSchemaDocuments SchemaDocumentsWithoutDateTimePaths()
        {
            var builder = new JsonSchemaBuilder();
            builder.Title("Ed-Fi.SimpleResource");
            builder.Description("A simple resource without datetime fields");
            builder.Schema("https://json-schema.org/draft/2020-12/schema");
            builder.AdditionalProperties(false);
            builder
                .Properties(
                    ("identifier", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                    ("description", new JsonSchemaBuilder().Type(SchemaValueType.String))
                )
                .Required("identifier");

            return new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("SimpleResource")
                .WithJsonSchemaForInsert(builder.Build()!)
                .WithDateTimeJsonPaths(Array.Empty<string>()) // No datetime paths
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        [SetUp]
        public async Task Setup()
        {
            string requestBody = """
                {
                    "identifier": "test-resource",
                    "description": "2009-05-01T09:00:00"
                }
                """;

            var frontEndRequest = new FrontendRequest(
                "ed-fi/simpleResources",
                Body: requestBody,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );

            _requestInfo = new(frontEndRequest, RequestMethod.POST)
            {
                ApiSchemaDocuments = SchemaDocumentsWithoutDateTimePaths(),
                ParsedBody = JsonNode.Parse(requestBody)!,
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("simpleResources"),
                    DocumentUuid: No.DocumentUuid
                ),
            };

            _requestInfo.ProjectSchema = _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
                new("ed-fi")
            )!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("simpleResources"))
                    ?? new JsonObject()
            );

            await Middleware().Execute(_requestInfo, _next);
        }

        [Test]
        public void Should_Not_Process_Any_Fields_When_No_DateTime_Paths_Configured()
        {
            // Even though description contains a datetime-like string, it should not be processed
            // because it's not in the DateTimeJsonPaths configuration
            var description = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.description",
                NullLogger.Instance
            );
            description!.GetValue<string>().Should().Be("2009-05-01T09:00:00");

            var identifier = _requestInfo.ParsedBody.SelectRequiredNodeFromPath(
                "$.identifier",
                NullLogger.Instance
            );
            identifier!.GetValue<string>().Should().Be("test-resource");
        }
    }
}
