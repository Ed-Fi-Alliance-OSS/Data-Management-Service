// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// Cursor recognition, the parameter-validation shell both paging modes answer a parameter fault
/// with, and the collection paging applied to request state.
/// </summary>
[TestFixture]
[Parallelizable]
public class ValidateQueryMiddlewareCursorTests
{
    private const int MaximumPageSize = 500;
    private const string TraceId = "cursor-trace-id";

    private static readonly string ValidToken = PageTokenCodec.Encode(new CursorRange(7, 42));

    /// <summary>
    /// A resource with one query field, so the unknown-query-field loop runs for real and can prove
    /// which parameters reached it.
    /// </summary>
    private static ApiSchemaDocuments NewApiSchemaDocuments() =>
        new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("AcademicWeek")
            .WithStartQueryFieldMapping()
            .WithQueryField("schoolId", [new("$.schoolId", "number")])
            .WithEndQueryFieldMapping()
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

    private static RequestInfo RequestInfoFor(params (string Key, string Value)[] queryParameters)
    {
        FrontendRequest frontendRequest = new(
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
        );

        RequestInfo requestInfo = new(frontendRequest, RequestMethod.GET, No.ServiceProvider)
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
        bool cursorParametersRecognized,
        params (string Key, string Value)[] queryParameters
    )
    {
        RequestInfo requestInfo = RequestInfoFor(queryParameters);

        IPipelineStep middleware = cursorParametersRecognized
            ? ValidateQueryMiddlewareTests.Middleware()
            : ValidateQueryMiddlewareTests.MiddlewareWithoutCursorRecognition();

        await middleware.Execute(requestInfo, NullNext);

        return requestInfo;
    }

    /// <summary>
    /// The parameter-validation shell, asserted whole so a partial regression cannot pass. Shared by
    /// the cursor and traditional fixtures, which answer a parameter fault identically.
    /// </summary>
    private static void AssertParameterValidationShell(RequestInfo requestInfo, string expectedError)
    {
        requestInfo.FrontendResponse.StatusCode.Should().Be(400);

        JsonNode body = requestInfo.FrontendResponse.Body!;

        body["detail"]!.GetValue<string>().Should().Be("Parameters supplied to the request were invalid.");
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:parameter-validation-failed");
        body["title"]!.GetValue<string>().Should().Be("Parameter Validation Failed");
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().Be(TraceId);
        body["validationErrors"]!.AsObject().Should().BeEmpty();
        body["errors"]!.AsArray().Select(error => error!.GetValue<string>()).Should().Equal(expectedError);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Rejected_Cursor_Request : ValidateQueryMiddlewareCursorTests
    {
        [TestCase("pageToken", "!!!", CursorRequestValidator.InvalidPageToken, TestName = "Phase 0")]
        [TestCase("offset", "1", CursorRequestValidator.OffsetWithPageToken, TestName = "Phase 1")]
        public async Task It_returns_the_parameter_validation_shell_for_token_phases(
            string parameter,
            string value,
            string expectedError
        )
        {
            RequestInfo requestInfo = await Execute(
                true,
                parameter == "pageToken" ? (parameter, value) : ("pageToken", ValidToken),
                parameter == "pageToken" ? ("offset", "1") : (parameter, value)
            );

            AssertParameterValidationShell(requestInfo, expectedError);
        }

        [Test]
        public async Task It_returns_the_parameter_validation_shell_for_the_relationship_phase()
        {
            AssertParameterValidationShell(
                await Execute(true, ("pageSize", "5")),
                CursorRequestValidator.PageTokenRequired
            );
        }

        [Test]
        public async Task It_returns_the_parameter_validation_shell_for_the_range_phase()
        {
            AssertParameterValidationShell(
                await Execute(true, ("pageToken", ValidToken), ("pageSize", "abc")),
                CursorRequestValidator.PageSizeOutOfRange(MaximumPageSize)
            );
        }

        [Test]
        public async Task It_leaves_collection_paging_unapplied()
        {
            RequestInfo requestInfo = await Execute(true, ("pageToken", "!!!"));

            requestInfo
                .CollectionPaging.Should()
                .Be(No.CollectionPaging, "a rejected request must not leave partially applied paging behind");
        }
    }

    /// <summary>
    /// Paging is determined before the change-version and query-field steps run, and either of
    /// those can still answer the request with 400. Request state must carry the paging of a
    /// request that was actually accepted, in both paging modes.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_Rejected_After_Its_Paging_Was_Determined : ValidateQueryMiddlewareCursorTests
    {
        [Test]
        public async Task It_leaves_cursor_paging_unapplied_for_an_unknown_query_field()
        {
            AssertRejectedWithoutPaging(
                await Execute(true, ("pageToken", ValidToken), ("notAQueryField", "1"))
            );
        }

        [Test]
        public async Task It_leaves_cursor_paging_unapplied_for_an_invalid_change_version()
        {
            AssertRejectedWithoutPaging(
                await Execute(true, ("pageToken", ValidToken), ("minChangeVersion", "abc"))
            );
        }

        [Test]
        public async Task It_leaves_cursor_paging_unapplied_for_a_query_field_of_the_wrong_type()
        {
            AssertRejectedWithoutPaging(
                await Execute(true, ("pageToken", ValidToken), ("schoolId", "notANumber"))
            );
        }

        [Test]
        public async Task It_leaves_traditional_paging_unapplied_for_an_unknown_query_field()
        {
            AssertRejectedWithoutPaging(await Execute(true, ("limit", "25"), ("notAQueryField", "1")));
        }

        private static void AssertRejectedWithoutPaging(RequestInfo requestInfo)
        {
            requestInfo
                .FrontendResponse.StatusCode.Should()
                .Be(400, "the arrangement must reach a rejection after paging was determined");

            requestInfo
                .CollectionPaging.Should()
                .Be(No.CollectionPaging, "a rejected request must not leave partially applied paging behind");
        }
    }

    /// <summary>
    /// A traditional pagination fault answers with the same parameter-validation shell as a cursor
    /// fault, while keeping the messages that predate cursor paging.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Traditional_Request_With_A_Fault : ValidateQueryMiddlewareCursorTests
    {
        private const string LimitOutOfRange =
            "Limit must be omitted or set to a numeric value between 0 and 500.";

        [TestCase("limit", "-1", LimitOutOfRange)]
        [TestCase("limit", "abc", LimitOutOfRange)]
        [TestCase("offset", "-1", "Offset must be a numeric value greater than or equal to 0.")]
        [TestCase("offset", "abc", "Offset must be a numeric value greater than or equal to 0.")]
        [TestCase("totalCount", "x", "TotalCount must be a boolean value.")]
        public async Task It_returns_the_parameter_validation_shell_with_the_existing_message(
            string parameter,
            string value,
            string expectedError
        )
        {
            AssertParameterValidationShell(await Execute(true, (parameter, value)), expectedError);
        }

        /// <summary>
        /// The traditional branch runs in the Change Query composition as well, so a pagination
        /// fault on /deletes or /keyChanges is answered the same way live GET-many answers it.
        /// </summary>
        [Test]
        public async Task It_returns_the_parameter_validation_shell_without_cursor_recognition()
        {
            AssertParameterValidationShell(await Execute(false, ("limit", "-1")), LimitOutOfRange);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Cursor_Request : ValidateQueryMiddlewareCursorTests
    {
        [Test]
        public async Task It_applies_the_decoded_range_and_page_size()
        {
            RequestInfo requestInfo = await Execute(true, ("pageToken", ValidToken), ("pageSize", "25"));

            requestInfo
                .CollectionPaging.Should()
                .Be(new CollectionPaging.Cursor(new CursorRange(7, 42), new PageSize(25)));
        }

        [Test]
        public async Task It_continues_the_pipeline()
        {
            RequestInfo requestInfo = await Execute(true, ("pageToken", ValidToken));

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        /// <summary>
        /// Asserted on a request the middleware accepts alongside a real resource filter, because
        /// query elements are assigned only at the accepting exit: a request answered before it
        /// carries no query elements whether or not the cursor parameters were excluded.
        /// </summary>
        [Test]
        public async Task It_does_not_treat_the_cursor_parameters_as_resource_filters()
        {
            RequestInfo requestInfo = await Execute(
                true,
                ("pageToken", ValidToken),
                ("pageSize", "25"),
                ("schoolId", "1")
            );

            requestInfo
                .QueryElements.Select(static queryElement => queryElement.QueryFieldName)
                .Should()
                .Equal("schoolId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Traditional_Request : ValidateQueryMiddlewareCursorTests
    {
        [Test]
        public async Task It_applies_traditional_paging_matching_the_parsed_parameters()
        {
            RequestInfo requestInfo = await Execute(
                true,
                ("limit", "25"),
                ("offset", "10"),
                ("totalCount", "true")
            );

            requestInfo
                .CollectionPaging.Should()
                .Be(new CollectionPaging.Traditional(requestInfo.PaginationParameters));
        }

        [Test]
        public async Task It_carries_the_requested_total_count()
        {
            RequestInfo requestInfo = await Execute(true, ("totalCount", "true"));

            requestInfo.CollectionPaging.IncludesTotalCount.Should().BeTrue();
        }

        [Test]
        public async Task It_reports_no_total_count_when_none_was_requested()
        {
            RequestInfo requestInfo = await Execute(true, ("limit", "25"));

            requestInfo.CollectionPaging.IncludesTotalCount.Should().BeFalse();
        }
    }

    /// <summary>
    /// The Change Query composition. Cursor parameters must reach the step that rejects them by
    /// name, so they are neither answered here with the resource-field wording nor accepted as
    /// resource filters.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Cursor_Parameters_Are_Not_Recognized : ValidateQueryMiddlewareCursorTests
    {
        [TestCase("pageToken")]
        [TestCase("pageSize")]
        public async Task It_does_not_answer_with_the_resource_field_wording(string parameter)
        {
            RequestInfo requestInfo = await Execute(false, (parameter, "5"));

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        /// <summary>
        /// Paired with a real resource filter so the request reaches the accepting exit, where query
        /// elements are assigned. A request answered earlier carries none of them either way.
        /// </summary>
        [TestCase("pageToken")]
        [TestCase("pageSize")]
        public async Task It_does_not_accept_them_as_resource_filters(string parameter)
        {
            RequestInfo requestInfo = await Execute(false, (parameter, "5"), ("schoolId", "1"));

            requestInfo
                .QueryElements.Select(static queryElement => queryElement.QueryFieldName)
                .Should()
                .Equal("schoolId");
        }

        [Test]
        public async Task It_does_not_run_cursor_validation()
        {
            RequestInfo requestInfo = await Execute(false, ("pageToken", "!!!"));

            requestInfo
                .FrontendResponse.Should()
                .Be(No.FrontendResponse, "an undecodable token is not this operation's complaint");
        }

        [Test]
        public async Task It_still_applies_traditional_paging()
        {
            RequestInfo requestInfo = await Execute(false, ("pageSize", "5"), ("limit", "25"));

            requestInfo
                .CollectionPaging.Should()
                .Be(new CollectionPaging.Traditional(requestInfo.PaginationParameters));
        }
    }
}
