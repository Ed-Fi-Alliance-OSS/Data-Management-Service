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
using EdFi.DataManagementService.Core.Telemetry;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// The query parameters GET-many and the Change Query endpoints ignore: which ones, how the request is
/// still validated and applied, and how the ignored names are reported.
/// </summary>
[TestFixture]
[Parallelizable]
public class ValidateQueryMiddlewareIgnoredParameterTests
{
    private const int MaximumPageSize = 500;
    private const string TraceId = "ignored-trace-id";

    private static ApiSchemaDocuments NewApiSchemaDocuments() =>
        new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("AcademicWeek")
            .WithStartQueryFieldMapping()
            .WithQueryField("schoolId", [new("$.schoolId", "number")])
            .WithQueryField("beginDate", [new("$.beginDate", "date")])
            .WithEndQueryFieldMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

    private static RequestInfo RequestInfoFor(params (string Key, string Value)[] queryParameters)
    {
        RequestInfo requestInfo = new(
            new FrontendRequest(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters.ToDictionary(
                    static parameter => parameter.Key,
                    static parameter => parameter.Value,
                    StringComparer.Ordinal
                ),
                TraceId: new TraceId(TraceId),
                RouteQualifiers: []
            ),
            RequestMethod.GET,
            ServiceProviderWithEffectiveTarget()
        )
        {
            ApiSchemaDocuments = NewApiSchemaDocuments(),
            PathComponents = new(
                ProjectEndpointName: new("ed-fi"),
                EndpointName: new("academicWeeks"),
                Operation: ResourcePathOperation.Collection.Instance
            ),
        };

        requestInfo.ProjectSchema = requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        requestInfo.ResourceSchema = new ResourceSchema(
            requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                ?? new JsonObject()
        );

        return requestInfo;
    }

    private static async Task<RequestInfo> Execute(
        bool changeQuery,
        IgnoredQueryParameterWarningTests.CapturingLogger? logger,
        params (string Key, string Value)[] queryParameters
    )
    {
        RequestInfo requestInfo = RequestInfoFor(queryParameters);

        IPipelineStep middleware = new ValidateQueryMiddleware(
            logger ?? new IgnoredQueryParameterWarningTests.CapturingLogger(),
            MaximumPageSize,
            _cursorParametersRecognized: !changeQuery,
            _resourceFiltersRecognized: !changeQuery,
            NoOpCollectionPagingTelemetry.Instance,
            _useLegacyDocumentIdOrderingForChangeQueries: false
        );

        await middleware.Execute(requestInfo, ValidateQueryMiddlewareTests.NextAnsweringOk(requestInfo));

        return requestInfo;
    }

    private protected static Task<RequestInfo> ExecuteGetMany(
        params (string Key, string Value)[] queryParameters
    ) => Execute(changeQuery: false, logger: null, queryParameters);

    private protected static Task<RequestInfo> ExecuteChangeQuery(
        params (string Key, string Value)[] queryParameters
    ) => Execute(changeQuery: true, logger: null, queryParameters);

    private static string? WarningOf(RequestInfo requestInfo) =>
        requestInfo.FrontendResponse.Headers.TryGetValue(
            IgnoredQueryParameterWarning.HeaderName,
            out string? value
        )
            ? value
            : null;

    private static string[] ErrorsOf(RequestInfo requestInfo) =>
        [
            .. requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>()),
        ];

    [TestFixture]
    [Parallelizable]
    public class Given_Get_Many_With_Unknown_Names : ValidateQueryMiddlewareIgnoredParameterTests
    {
        [Test]
        public async Task It_succeeds_and_applies_only_the_known_filter()
        {
            RequestInfo requestInfo = await ExecuteGetMany(
                ("studentUniqueld", "123"),
                ("schoolId", "255901"),
                ("useJoinAuth", "true")
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo
                .QueryElements.Select(queryElement => (queryElement.QueryFieldName, queryElement.Value))
                .Should()
                .Equal(("schoolId", "255901"));
            WarningOf(requestInfo).Should().Be("Ignored query parameters: studentUniqueld, useJoinAuth");
        }

        [Test]
        public async Task It_sends_no_warning_and_logs_nothing_when_nothing_was_ignored()
        {
            IgnoredQueryParameterWarningTests.CapturingLogger logger = new();

            RequestInfo requestInfo = await Execute(
                changeQuery: false,
                logger,
                ("schoolId", "255901"),
                ("limit", "5"),
                ("MINCHANGEVERSION", "1")
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            WarningOf(requestInfo).Should().BeNull();
            logger.Entries.Should().NotContain(entry => entry.State.ContainsKey("IgnoredQueryParameters"));
        }

        [TestCase("before")]
        [TestCase("after")]
        public async Task It_still_rejects_a_malformed_filter_value(string unknownPosition)
        {
            (string, string)[] queryParameters =
                unknownPosition == "before"
                    ? [("notAField", "1"), ("beginDate", "notadate")]
                    : [("beginDate", "notadate"), ("notAField", "1")];

            RequestInfo requestInfo = await ExecuteGetMany(queryParameters);

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            requestInfo.FrontendResponse.Body!["validationErrors"]!["$.beginDate"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The value 'notadate' is not valid for beginDate.");
            WarningOf(requestInfo).Should().Be("Ignored query parameters: notAField");
        }

        [TestCase("limit", "abc", "Limit must be omitted or set to a numeric value between 0 and 500.")]
        [TestCase("offset", "-1", "Offset must be a numeric value greater than or equal to 0.")]
        [TestCase(
            "minChangeVersion",
            "abc",
            "MinChangeVersion must be a numeric value greater than or equal to 0."
        )]
        public async Task It_still_rejects_a_malformed_control_value(
            string control,
            string value,
            string expectedError
        )
        {
            RequestInfo requestInfo = await ExecuteGetMany(("notAField", "1"), (control, value));

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            ErrorsOf(requestInfo).Should().Contain(expectedError);
            WarningOf(requestInfo).Should().Be("Ignored query parameters: notAField");
        }

        [Test]
        public async Task It_reports_every_unknown_name_not_only_the_first()
        {
            RequestInfo requestInfo = await ExecuteGetMany(("first", "1"), ("second", "2"), ("third", "3"));

            WarningOf(requestInfo).Should().Be("Ignored query parameters: first, second, third");
        }

        [Test]
        public async Task It_still_validates_a_known_filter_after_more_names_than_are_listed()
        {
            (string, string)[] queryParameters =
            [
                .. Enumerable.Range(1, 12).Select(index => ($"unknown{index}", "x")),
                ("schoolId", "notANumber"),
            ];

            RequestInfo requestInfo = await ExecuteGetMany(queryParameters);

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            requestInfo.FrontendResponse.Body!["validationErrors"]!
                .AsObject()
                .Should()
                .ContainKey("$.schoolId");
            WarningOf(requestInfo)
                .Should()
                .Be(
                    "Ignored query parameters: unknown1, unknown2, unknown3, unknown4, unknown5, unknown6, "
                        + "unknown7, unknown8, unknown9, unknown10, (and 2 more)"
                );
        }

        [Test]
        public async Task It_still_applies_a_known_filter_after_more_names_than_are_listed()
        {
            (string, string)[] queryParameters =
            [
                .. Enumerable.Range(1, 12).Select(index => ($"unknown{index}", "x")),
                ("schoolId", "255901"),
            ];

            RequestInfo requestInfo = await ExecuteGetMany(queryParameters);

            requestInfo.QueryElements.Select(queryElement => queryElement.Value).Should().Equal("255901");
        }

        [Test]
        public async Task It_logs_the_rendered_names_without_their_values()
        {
            IgnoredQueryParameterWarningTests.CapturingLogger logger = new();

            await Execute(changeQuery: false, logger, ("bad\r\nname", "secret-value"));

            IgnoredQueryParameterWarningTests.CapturingLogger.Entry entry = logger
                .Entries.Where(entry => entry.State.ContainsKey("IgnoredQueryParameters"))
                .Should()
                .ContainSingle()
                .Subject;
            entry.State["IgnoredQueryParameters"].Should().Be("badname");
            entry.Message.Should().NotContain("secret-value").And.NotContain("\r").And.NotContain("\n");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Change_Query_With_Resource_Filters : ValidateQueryMiddlewareIgnoredParameterTests
    {
        [Test]
        public async Task It_ignores_a_well_formed_filter_and_reports_it()
        {
            RequestInfo requestInfo = await ExecuteChangeQuery(("schoolId", "255901"));

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.QueryElements.Should().BeEmpty();
            WarningOf(requestInfo).Should().Be("Ignored query parameters: schoolId");
        }

        // The ODS/API does not bind a resource filter on a Change Query, so its value is never judged.
        [Test]
        public async Task It_ignores_a_malformed_filter_value_rather_than_rejecting_it()
        {
            RequestInfo requestInfo = await ExecuteChangeQuery(("beginDate", "notadate"));

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.QueryElements.Should().BeEmpty();
            WarningOf(requestInfo).Should().Be("Ignored query parameters: beginDate");
        }

        [Test]
        public async Task It_keeps_the_limit_offset_and_change_version_semantics()
        {
            RequestInfo requestInfo = await ExecuteChangeQuery(
                ("schoolId", "255901"),
                ("limit", "25"),
                ("offset", "10"),
                ("totalCount", "true"),
                ("minChangeVersion", "5"),
                ("maxChangeVersion", "9")
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            requestInfo.PaginationParameters.Limit.Should().Be(25);
            requestInfo.PaginationParameters.Offset.Should().Be(10);
            requestInfo.PaginationParameters.TotalCount.Should().BeTrue();
            requestInfo.ChangeVersionRange.MinChangeVersion.Should().Be(5);
            requestInfo.ChangeVersionRange.MaxChangeVersion.Should().Be(9);
            requestInfo
                .CollectionPaging.Should()
                .Be(new CollectionPaging.Traditional(requestInfo.PaginationParameters));
            WarningOf(requestInfo).Should().Be("Ignored query parameters: schoolId");
        }

        [Test]
        public async Task It_still_rejects_a_malformed_limit()
        {
            RequestInfo requestInfo = await ExecuteChangeQuery(("schoolId", "255901"), ("limit", "abc"));

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            ErrorsOf(requestInfo)
                .Should()
                .Equal("Limit must be omitted or set to a numeric value between 0 and 500.");
            WarningOf(requestInfo).Should().Be("Ignored query parameters: schoolId");
        }

        [Test]
        public async Task It_lists_filters_and_unknown_names_in_request_order()
        {
            RequestInfo requestInfo = await ExecuteChangeQuery(
                ("notAField", "x"),
                ("schoolId", "1"),
                ("anotherUnknown", "y")
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            WarningOf(requestInfo)
                .Should()
                .Be("Ignored query parameters: notAField, schoolId, anotherUnknown");
        }

        // The cursor parameters are rejected by name rather than ignored, so the warning names only the
        // ignored filter.
        [Test]
        public async Task It_rejects_a_cursor_parameter_and_reports_only_the_ignored_filter()
        {
            RequestInfo requestInfo = await ExecuteChangeQuery(("schoolId", "1"), ("pageSize", "5"));

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            ErrorsOf(requestInfo)
                .Should()
                .Equal("The query field 'pageSize' is not valid for this Change Query endpoint.");
            WarningOf(requestInfo).Should().Be("Ignored query parameters: schoolId");
        }
    }
}
