// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Telemetry;
using EdFi.DataManagementService.Core.Tests.Unit.Handler;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// The partitions pipeline's request validation: which parameters it owns, which faults it answers,
/// in what order, and what a request it accepts leaves behind in request state.
/// </summary>
[TestFixture]
[Parallelizable]
public class ValidatePartitionQueryMiddlewareTests
{
    private const int DefaultPartitionCount = 12;
    private const string TraceId = "partition-trace-id";

    /// <summary>
    /// A resource with two query fields, one of them typed, so the unknown-field and bad-value
    /// branches both run for real rather than over an empty field set.
    /// </summary>
    private static ApiSchemaDocuments NewApiSchemaDocuments() =>
        new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("AcademicWeek")
            .WithStartQueryFieldMapping()
            .WithQueryField("schoolId", [new("$.schoolId", "number")])
            .WithQueryField("weekIdentifier", [new("$.weekIdentifier", "string")])
            .WithEndQueryFieldMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

    private static RequestInfo RequestInfoFor(params (string Key, string Value)[] queryParameters)
    {
        FrontendRequest frontendRequest = new(
            Path: "/ed-fi/academicWeeks/partitions",
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
        );

        RequestInfo requestInfo = new(frontendRequest, RequestMethod.GET, No.ServiceProvider)
        {
            ApiSchemaDocuments = NewApiSchemaDocuments(),
            PathComponents = new(
                ProjectEndpointName: new("ed-fi"),
                EndpointName: new("academicWeeks"),
                Operation: ResourcePathOperation.Partitions.Instance
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

    private static Task<RequestInfo> Execute(params (string Key, string Value)[] queryParameters) =>
        Execute(collectionPagingTelemetry: null, queryParameters);

    private static async Task<RequestInfo> Execute(
        ICollectionPagingTelemetry? collectionPagingTelemetry,
        params (string Key, string Value)[] queryParameters
    )
    {
        RequestInfo requestInfo = RequestInfoFor(queryParameters);

        IPipelineStep middleware = new ValidatePartitionQueryMiddleware(
            NullLogger.Instance,
            DefaultPartitionCount,
            collectionPagingTelemetry ?? NoOpCollectionPagingTelemetry.Instance
        );

        await middleware.Execute(requestInfo, NullNext);

        return requestInfo;
    }

    /// <summary>
    /// The parameter-validation shell, asserted whole so a partial regression cannot pass. The media
    /// type is asserted because nothing at the call site states it: it comes from the FrontendResponse
    /// default.
    /// </summary>
    private static void AssertParameterValidationShell(
        RequestInfo requestInfo,
        params string[] expectedErrors
    )
    {
        requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        requestInfo.FrontendResponse.ContentType.Should().Be("application/json");

        JsonNode body = requestInfo.FrontendResponse.Body!;

        body["detail"]!.GetValue<string>().Should().Be("Parameters supplied to the request were invalid.");
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:parameter-validation-failed");
        body["title"]!.GetValue<string>().Should().Be("Parameter Validation Failed");
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().Be(TraceId);
        body["validationErrors"]!.AsObject().Should().BeEmpty();
        body["errors"]!.AsArray().Select(error => error!.GetValue<string>()).Should().Equal(expectedErrors);
    }

    private static void AssertNothingApplied(RequestInfo requestInfo)
    {
        requestInfo
            .RequestedPartitionCount.Should()
            .BeNull("a rejected request must not carry a count a handler could act on");
        requestInfo.QueryElements.Should().BeEmpty();
        requestInfo.ChangeVersionRange.Should().Be(ChangeVersionRange.None);
        requestInfo
            .CollectionPaging.Should()
            .Be(No.CollectionPaging, "a partitions request has no page in any outcome");
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Malformed_Or_Out_Of_Range_Count : ValidatePartitionQueryMiddlewareTests
    {
        [TestCase("abc", TestName = "not a number")]
        [TestCase("", TestName = "present but blank")]
        [TestCase("0", TestName = "below the minimum")]
        [TestCase("201", TestName = "above the maximum")]
        [TestCase("-1", TestName = "negative")]
        public async Task It_returns_the_parameter_validation_shell(string number)
        {
            RequestInfo requestInfo = await Execute(("number", number));

            AssertParameterValidationShell(requestInfo, PartitionRequestValidator.NumberOutOfRange);
            AssertNothingApplied(requestInfo);
        }

        // The count controls the calculation itself, so it is reported alone: a client that fixes the
        // count and resends still learns about the reserved parameter, and one that fixes only the
        // reserved parameter learns nothing useful.
        [Test]
        public async Task It_suppresses_the_reserved_parameter_phase()
        {
            AssertParameterValidationShell(
                await Execute(("number", "0"), ("limit", "5")),
                PartitionRequestValidator.NumberOutOfRange
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Reserved_Paging_Parameters : ValidatePartitionQueryMiddlewareTests
    {
        [TestCase("pageToken", "abc")]
        [TestCase("pageSize", "5")]
        [TestCase("limit", "5")]
        [TestCase("offset", "0")]
        [TestCase("totalCount", "true")]
        public async Task It_names_the_parameter_that_does_not_apply(string parameter, string value)
        {
            RequestInfo requestInfo = await Execute((parameter, value));

            AssertParameterValidationShell(
                requestInfo,
                PartitionRequestValidator.UnsupportedParameter(parameter)
            );
            AssertNothingApplied(requestInfo);
        }

        // Several are independent mistakes, so all of them are reported in one response rather than
        // over as many round trips, in the canonical order the validator publishes.
        [Test]
        public async Task It_reports_every_reserved_parameter_in_canonical_order()
        {
            AssertParameterValidationShell(
                await Execute(("totalCount", "true"), ("limit", "5"), ("pageToken", "abc")),
                PartitionRequestValidator.UnsupportedParameter("pageToken"),
                PartitionRequestValidator.UnsupportedParameter("limit"),
                PartitionRequestValidator.UnsupportedParameter("totalCount")
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Unknown_Query_Field : ValidatePartitionQueryMiddlewareTests
    {
        [Test]
        public async Task It_returns_the_bad_request_shell_naming_the_field()
        {
            RequestInfo requestInfo = await Execute(("notAField", "1"));

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);

            JsonNode body = requestInfo.FrontendResponse.Body!;

            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
            body["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal("The query field 'notAField' is not valid for this resource.");
            AssertNothingApplied(requestInfo);
        }

        // Filters are matched before the partition phase, because the reserved names are excluded from
        // filter matching and that exclusion is what lets '?limit=5' be reported as a parameter that
        // does not apply rather than as an unknown field. The consequence is that a request carrying
        // both is answered with the unknown field alone.
        [Test]
        public async Task It_answers_before_the_reserved_parameter_phase()
        {
            RequestInfo requestInfo = await Execute(("notAField", "1"), ("limit", "5"));

            requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal("The query field 'notAField' is not valid for this resource.");
        }

        // Filter matching also precedes the count phase, so a request that is wrong in both ways is
        // answered with the field. The count error is suppressed, not merged.
        [Test]
        public async Task It_answers_before_the_partition_count_phase()
        {
            RequestInfo requestInfo = await Execute(("number", "abc"), ("notAField", "1"));

            requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:bad-request");
            requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal("The query field 'notAField' is not valid for this resource.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Filter_Value_Of_The_Wrong_Type : ValidatePartitionQueryMiddlewareTests
    {
        [Test]
        public async Task It_returns_the_data_validation_shell_keyed_by_document_path()
        {
            RequestInfo requestInfo = await Execute(("schoolId", "notANumber"));

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);

            JsonNode body = requestInfo.FrontendResponse.Body!;

            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:data-validation-failed");
            body["validationErrors"]!.AsObject().Should().ContainKey("$.schoolId");
            AssertNothingApplied(requestInfo);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Faulty_Change_Version_Window : ValidatePartitionQueryMiddlewareTests
    {
        [Test]
        public async Task It_returns_the_parameter_validation_shell()
        {
            RequestInfo requestInfo = await Execute(("minChangeVersion", "notANumber"));

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:bad-request:parameter-validation-failed");
            AssertNothingApplied(requestInfo);
        }

        // The window is validated ahead of the count, so a request faulty in both ways is answered
        // with the window error alone. Both shells are parameter-validation, so the errors array is
        // what distinguishes them.
        [Test]
        public async Task It_answers_before_the_partition_count_phase()
        {
            RequestInfo requestInfo = await Execute(("number", "abc"), ("minChangeVersion", "notANumber"));

            requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .NotContain(PartitionRequestValidator.NumberOutOfRange)
                .And.ContainSingle();
            AssertNothingApplied(requestInfo);
        }

        // The window is validated ahead of the resource filters as well, which is what makes this
        // operation answer a query string faulty in both ways with the same problem type GET-many
        // answers it with. A client that discriminates on type does not have to know which of the two
        // sibling operations it called.
        [Test]
        public async Task It_answers_before_the_resource_filter_phase()
        {
            RequestInfo requestInfo = await Execute(("minChangeVersion", "notANumber"), ("notAField", "1"));

            requestInfo.FrontendResponse.Body!["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:bad-request:parameter-validation-failed");
            requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .NotContain("The query field 'notAField' is not valid for this resource.")
                .And.ContainSingle();
            AssertNothingApplied(requestInfo);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Accepted_Request : ValidatePartitionQueryMiddlewareTests
    {
        [Test]
        public async Task It_applies_the_client_count_when_one_was_supplied()
        {
            RequestInfo requestInfo = await Execute(("number", "37"));

            requestInfo.RequestedPartitionCount.Should().Be(37);
        }

        [Test]
        public async Task It_applies_the_configured_default_when_the_count_was_omitted()
        {
            RequestInfo requestInfo = await Execute();

            requestInfo.RequestedPartitionCount.Should().Be(DefaultPartitionCount);
        }

        [Test]
        public async Task It_applies_the_filters_and_the_change_version_window()
        {
            RequestInfo requestInfo = await Execute(
                ("schoolId", "255901"),
                ("minChangeVersion", "10"),
                ("maxChangeVersion", "20"),
                ("number", "4")
            );

            requestInfo
                .QueryElements.Select(queryElement => queryElement.QueryFieldName)
                .Should()
                .Equal("schoolId");
            requestInfo.ChangeVersionRange.MinChangeVersion.Should().Be(10);
            requestInfo.ChangeVersionRange.MaxChangeVersion.Should().Be(20);
            requestInfo.RequestedPartitionCount.Should().Be(4);
        }

        // A partitions request has no page, so nothing on this pipeline may leave a paging choice a
        // handler could act on.
        [Test]
        public async Task It_never_applies_collection_paging()
        {
            RequestInfo requestInfo = await Execute(("number", "4"));

            requestInfo.CollectionPaging.Should().Be(No.CollectionPaging);
        }

        [Test]
        public async Task It_continues_into_the_rest_of_the_pipeline()
        {
            RequestInfo requestInfo = RequestInfoFor(("number", "4"));
            var reachedNext = false;

            await new ValidatePartitionQueryMiddleware(
                NullLogger.Instance,
                DefaultPartitionCount,
                NoOpCollectionPagingTelemetry.Instance
            ).Execute(
                requestInfo,
                () =>
                {
                    reachedNext = true;
                    return Task.CompletedTask;
                }
            );

            reachedNext.Should().BeTrue();
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    /// <summary>
    /// What a request this step answers contributes to the collection-paging metric.
    /// </summary>
    /// <remarks>
    /// The paging mode is the partition literal on every exit, because this step is composed only into
    /// the partitions pipeline. Unlike the GET-many validator it has no second construction site to
    /// isolate.
    /// </remarks>
    [TestFixture]
    [Parallelizable]
    public class Given_Collection_Paging_Telemetry_For_A_Rejection : ValidatePartitionQueryMiddlewareTests
    {
        private static async Task<RecordingCollectionPagingTelemetry> ExecuteRecording(
            params (string Key, string Value)[] queryParameters
        )
        {
            RecordingCollectionPagingTelemetry telemetry = new();

            RequestInfo requestInfo = RequestInfoFor(queryParameters);
            requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Mssql);

            await new ValidatePartitionQueryMiddleware(
                NullLogger.Instance,
                DefaultPartitionCount,
                telemetry
            ).Execute(requestInfo, NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);

            return telemetry;
        }

        private static readonly TestCaseData[] _rejections =
        [
            new TestCaseData(new[] { ("number", "0") }).SetName("{m}(partition count fault)"),
            new TestCaseData(new[] { ("minChangeVersion", "abc") }).SetName("{m}(change-version fault)"),
            new TestCaseData(new[] { ("notAField", "1") }).SetName("{m}(unknown query field)"),
            new TestCaseData(new[] { ("schoolId", "not-a-number") }).SetName("{m}(invalid filter value)"),
        ];

        [TestCaseSource(nameof(_rejections))]
        public async Task It_counts_every_rejecting_exit_exactly_once(
            (string Key, string Value)[] queryParameters
        )
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteRecording(queryParameters);

            CollectionPagingMeasurement measurement = telemetry.Single;

            measurement.Kind.Should().Be(CollectionPagingMeasurementKind.ValidationRejected);
            measurement.PagingMode.Should().Be("partition");
            measurement.CommandCategory.Should().Be("none");
            measurement.Provider.Should().Be("sqlserver");
            measurement.Outcome.Should().Be("validation_rejected");
        }

        // Nothing executed, so a duration sample would report the cost of parsing a query string as a
        // boundary-command latency.
        [Test]
        public async Task It_records_no_duration_or_counts_for_a_rejection()
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteRecording(("number", "0"));

            telemetry.Single.Duration.Should().BeNull();
            telemetry.Single.Requested.Should().BeNull();
            telemetry.Single.Returned.Should().BeNull();
        }

        [Test]
        public async Task It_counts_nothing_for_a_request_it_accepts()
        {
            RecordingCollectionPagingTelemetry telemetry = new();

            RequestInfo requestInfo = await Execute(telemetry, ("number", "4"));

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            telemetry.Measurements.Should().BeEmpty();
        }
    }
}
