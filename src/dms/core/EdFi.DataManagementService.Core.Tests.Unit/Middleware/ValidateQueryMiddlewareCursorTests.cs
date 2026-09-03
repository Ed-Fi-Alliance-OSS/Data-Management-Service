// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Tests.Unit.Handler;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
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

    private static readonly string ValidToken = PageTokenCodec.Encode(
        new CursorRange(7, 42),
        PageOrderingMode.DocumentId
    );

    /// <summary>
    /// The token a max-bearing windowed page hands out. Same bounds as <see cref="ValidToken"/>, so the
    /// only difference between the two is the anchor each was issued for.
    /// </summary>
    private static readonly string WindowedToken = PageTokenCodec.Encode(
        new CursorRange(7, 42),
        PageOrderingMode.ContentVersion
    );

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

    private static RequestInfo RequestInfoFor(
        EffectiveTargetKind targetKind,
        params (string Key, string Value)[] queryParameters
    )
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

        RequestInfo requestInfo = new(
            frontendRequest,
            RequestMethod.GET,
            ServiceProviderWithEffectiveTarget(targetKind)
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

    private static Task<RequestInfo> Execute(
        bool cursorParametersRecognized,
        params (string Key, string Value)[] queryParameters
    ) =>
        Execute(
            cursorParametersRecognized
                ? ValidateQueryMiddlewareTests.Middleware()
                : ValidateQueryMiddlewareTests.MiddlewareWithoutCursorRecognition(),
            queryParameters
        );

    /// <summary>
    /// The composition under test is supplied directly by the fixtures that need one this file does not
    /// otherwise build — the deployment running with the page-ordering kill switch on.
    /// </summary>
    private static Task<RequestInfo> Execute(
        IPipelineStep middleware,
        params (string Key, string Value)[] queryParameters
    ) => Execute(middleware, EffectiveTargetKind.Primary, queryParameters);

    private static async Task<RequestInfo> Execute(
        IPipelineStep middleware,
        EffectiveTargetKind targetKind,
        params (string Key, string Value)[] queryParameters
    )
    {
        RequestInfo requestInfo = RequestInfoFor(targetKind, queryParameters);

        await middleware.Execute(requestInfo, NullNext);

        return requestInfo;
    }

    /// <summary>
    /// The parameter-validation shell, asserted whole so a partial regression cannot pass. Shared by
    /// the cursor and traditional fixtures, which answer a parameter fault identically. The expected
    /// messages are ordered: a cursor fault reports exactly one, a traditional fault reports every
    /// faulty parameter.
    ///
    /// The media type is asserted alongside the body because nothing at the call site states it: it
    /// comes from the FrontendResponse default, and the frontend appends the charset that makes it
    /// the documented `application/json; charset=utf-8` response type.
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

        /// <summary>
        /// The change-version window is parsed ahead of cursor validation, because the page anchor is
        /// resolved from it, but its errors are still reported behind a cursor fault. Both families
        /// answer with this same shell, so the reported message is the only thing separating
        /// "answered here" from "answered by the window".
        /// </summary>
        [Test]
        public async Task It_reports_only_the_cursor_fault_when_the_window_is_also_invalid()
        {
            AssertParameterValidationShell(
                await Execute(true, ("pageToken", "!!!"), ("minChangeVersion", "abc")),
                CursorRequestValidator.InvalidPageToken
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
    /// fault, while keeping the messages that predate cursor paging, and is answered ahead of the
    /// change-version parameters.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Traditional_Request_With_A_Fault : ValidateQueryMiddlewareCursorTests
    {
        private const string LimitOutOfRange =
            "Limit must be omitted or set to a numeric value between 0 and 500.";

        private const string OffsetNegative = "Offset must be a numeric value greater than or equal to 0.";

        private const string TotalCountNotABoolean = "TotalCount must be a boolean value.";

        private const string MinChangeVersionNotNumeric =
            "MinChangeVersion must be a numeric value greater than or equal to 0.";

        [TestCase("limit", "-1", LimitOutOfRange)]
        [TestCase("limit", "abc", LimitOutOfRange)]
        [TestCase("offset", "-1", OffsetNegative)]
        [TestCase("offset", "abc", OffsetNegative)]
        [TestCase("totalCount", "x", TotalCountNotABoolean)]
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
        /// fault on /deletes or /keyChanges is answered the same way live GET-many answers it. All
        /// three parameters are covered rather than one representative, because this composition has
        /// no cursor path and the traditional branch is the only thing answering them.
        /// </summary>
        [TestCase("limit", "-1", LimitOutOfRange)]
        [TestCase("offset", "-1", OffsetNegative)]
        [TestCase("totalCount", "x", TotalCountNotABoolean)]
        public async Task It_returns_the_parameter_validation_shell_without_cursor_recognition(
            string parameter,
            string value,
            string expectedError
        )
        {
            AssertParameterValidationShell(await Execute(false, (parameter, value)), expectedError);
        }

        /// <summary>
        /// The pagination rules are evaluated together rather than exclusively, so all three faults
        /// are reported in one response. Pinned on this composition because change-queries.md states
        /// the ordering as a contract of /deletes and /keyChanges, which is what recognizes no cursor
        /// parameters. See change-queries.md, "Parameter Validation Failures".
        /// </summary>
        [Test]
        public async Task It_reports_every_pagination_fault_in_the_documented_order()
        {
            AssertParameterValidationShell(
                await Execute(false, ("offset", "-1"), ("limit", "-1"), ("totalCount", "x")),
                OffsetNegative,
                LimitOutOfRange,
                TotalCountNotABoolean
            );
        }

        /// <summary>
        /// Pagination is validated ahead of the change-version parameters and a fault there is
        /// answered immediately, so the change-version values are never examined. Both families
        /// answer with this same shell, so the reported messages are the only thing separating
        /// "accepted" from "never reached".
        /// </summary>
        [Test]
        public async Task It_reports_only_the_pagination_fault_when_change_version_is_also_invalid()
        {
            AssertParameterValidationShell(
                await Execute(true, ("limit", "-1"), ("minChangeVersion", "abc")),
                LimitOutOfRange
            );
        }

        /// <summary>
        /// The same change-version value on its own, so the test above cannot pass because the
        /// parameter is ignored outright rather than deferred behind the pagination fault.
        /// </summary>
        [Test]
        public async Task It_reports_the_change_version_fault_once_pagination_is_clean()
        {
            AssertParameterValidationShell(
                await Execute(true, ("minChangeVersion", "abc")),
                MinChangeVersionNotNumeric
            );
        }

        /// <summary>
        /// The traditional twin of the cursor fixture's paging guard. Both properties are asserted
        /// because they are kept unapplied by different mechanisms: CollectionPaging by the early
        /// return preceding its single assignment site, and PaginationParameters by the errors-empty
        /// guard inside the parsing helper, which is the only thing standing between a rejected
        /// request and parsed pagination a Change Query handler would read directly.
        /// </summary>
        [Test]
        public async Task It_leaves_paging_unapplied()
        {
            RequestInfo requestInfo = await Execute(true, ("limit", "-1"));

            requestInfo
                .CollectionPaging.Should()
                .Be(No.CollectionPaging, "a rejected request must not leave partially applied paging behind");

            requestInfo
                .PaginationParameters.Should()
                .Be(No.PaginationParameters, "a rejected request must not leave parsed pagination behind");
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
        /// The anchor a cursor walk's bounds are expressed in reaches request state alongside the
        /// decoded range, so the compiler and the token emitter read one resolution rather than each
        /// deriving their own.
        /// </summary>
        [Test]
        public async Task It_carries_the_content_version_anchor_of_a_windowed_walk()
        {
            RequestInfo requestInfo = await Execute(
                true,
                ("pageToken", WindowedToken),
                ("maxChangeVersion", "200")
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
        }

        [Test]
        public async Task It_carries_the_document_id_anchor_of_an_unwindowed_walk()
        {
            RequestInfo requestInfo = await Execute(true, ("pageToken", ValidToken));

            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
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

    /// <summary>
    /// The anchor the request resolves and the anchor its token was issued for, compared through the
    /// real pipeline: the window is parsed here, the anchor is resolved from it here, and a
    /// disagreement is answered with the standard invalid-token response rather than by serving bounds
    /// read against the wrong column.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Page_Token_Replayed_Under_A_Different_Window : ValidateQueryMiddlewareCursorTests
    {
        [Test]
        public async Task It_rejects_a_windowed_token_replayed_without_the_window()
        {
            AssertParameterValidationShell(
                await Execute(true, ("pageToken", WindowedToken)),
                CursorRequestValidator.InvalidPageToken
            );
        }

        [Test]
        public async Task It_rejects_an_unwindowed_token_replayed_with_a_window()
        {
            AssertParameterValidationShell(
                await Execute(true, ("pageToken", ValidToken), ("maxChangeVersion", "200")),
                CursorRequestValidator.InvalidPageToken
            );
        }

        [Test]
        public async Task It_accepts_a_windowed_token_replayed_with_its_window()
        {
            RequestInfo requestInfo = await Execute(
                true,
                ("pageToken", WindowedToken),
                ("minChangeVersion", "100"),
                ("maxChangeVersion", "200")
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo
                .CollectionPaging.Should()
                .Be(new CollectionPaging.Cursor(new CursorRange(7, 42), new PageSize(MaximumPageSize)));
        }

        /// <summary>
        /// Moving the ceiling mid-walk is accepted, which is the published rule. What the marker
        /// compares is the anchor the window resolves, not the window itself, so a ceiling that moved is
        /// still a ceiling and the token is still read in the units it names. The bounds pass through
        /// untouched — they are a position in the anchor's sequence, not a slice of the window — while
        /// the window the request carries is the new one, so the same token position now reads a
        /// different result set. That is a filter change like any other, not a token fault.
        /// </summary>
        [Test]
        public async Task It_accepts_a_windowed_token_replayed_under_a_moved_maximum()
        {
            RequestInfo requestInfo = await Execute(
                true,
                ("pageToken", WindowedToken),
                ("minChangeVersion", "100"),
                ("maxChangeVersion", "999")
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
            requestInfo
                .CollectionPaging.Should()
                .Be(new CollectionPaging.Cursor(new CursorRange(7, 42), new PageSize(MaximumPageSize)));
            requestInfo.ChangeVersionRange.Should().Be(new ChangeVersionRange(100L, 999L));
        }

        /// <summary>
        /// A min-only window keeps the <c>DocumentId</c> anchor, because an update inside a window that
        /// is open above moves a row past a <c>ContentVersion</c> anchor while it remains eligible.
        /// </summary>
        [Test]
        public async Task It_accepts_an_unwindowed_token_for_a_min_only_request()
        {
            RequestInfo requestInfo = await Execute(
                true,
                ("pageToken", ValidToken),
                ("minChangeVersion", "100")
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        /// <summary>
        /// '?maxChangeVersion=' parses to null, so it is not a max-bearing window and does not move the
        /// anchor. The token the request must carry is therefore the unwindowed one.
        /// </summary>
        [Test]
        public async Task It_treats_an_empty_maximum_as_no_window()
        {
            RequestInfo accepted = await Execute(true, ("pageToken", ValidToken), ("maxChangeVersion", ""));

            accepted.FrontendResponse.Should().Be(No.FrontendResponse);
            accepted.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);

            AssertParameterValidationShell(
                await Execute(true, ("pageToken", WindowedToken), ("maxChangeVersion", "")),
                CursorRequestValidator.InvalidPageToken
            );
        }

        /// <summary>
        /// An unparseable maximum parses to null for the same reason an empty one does — but unlike an
        /// empty one it is a fault, and a request carrying it resolves no anchor rather than the
        /// <c>DocumentId</c> anchor its null bounds would otherwise imply. There is nothing for the
        /// token's marker to disagree with, so the window's own fault is what the client is told about
        /// and the token it is holding is not blamed for a typo beside it.
        /// </summary>
        /// <remarks>
        /// Both tokens are asserted because the anchor comparison is what is being skipped: under the
        /// earlier rule the windowed token was answered as invalid here while the unwindowed one
        /// reached the window fault, so a test using only one of them could not tell the two rules
        /// apart.
        /// </remarks>
        [Test]
        public async Task It_reports_the_window_fault_for_an_unparseable_maximum_whichever_token_accompanies_it()
        {
            const string WindowFault = "MaxChangeVersion must be a numeric value greater than or equal to 0.";

            AssertParameterValidationShell(
                await Execute(true, ("pageToken", WindowedToken), ("maxChangeVersion", "abc")),
                WindowFault
            );

            AssertParameterValidationShell(
                await Execute(true, ("pageToken", ValidToken), ("maxChangeVersion", "abc")),
                WindowFault
            );
        }

        /// <summary>
        /// The rule is about a faulty window, not about an unparseable bound. An inverted window parses
        /// both of its bounds cleanly and still cannot be accepted, so it resolves no anchor either and
        /// is reported by its own message rather than by the token that accompanies it.
        /// </summary>
        [Test]
        public async Task It_reports_the_window_fault_for_an_inverted_window()
        {
            AssertParameterValidationShell(
                await Execute(
                    true,
                    ("pageToken", ValidToken),
                    ("minChangeVersion", "300"),
                    ("maxChangeVersion", "200")
                ),
                "MinChangeVersion must be less than or equal to MaxChangeVersion."
            );
        }

        /// <summary>
        /// Skipping the anchor comparison for a faulty window does not soften phase 0 itself: a token
        /// that cannot decode is a fault of its own and is still reported as one, whatever the window
        /// beside it says.
        /// </summary>
        [Test]
        public async Task It_still_reports_an_undecodable_token_under_an_unparseable_maximum()
        {
            AssertParameterValidationShell(
                await Execute(true, ("pageToken", "!!!"), ("maxChangeVersion", "abc")),
                CursorRequestValidator.InvalidPageToken
            );
        }

        /// <summary>
        /// A deployment running with the page-ordering kill switch on resolves <c>DocumentId</c> for
        /// every window, so it keeps issuing and accepting the <c>d</c>-marked tokens its own pages
        /// hand out instead of breaking a walk the moment the switch is turned on.
        /// </summary>
        [Test]
        public async Task It_accepts_an_unwindowed_token_with_a_window_under_legacy_ordering()
        {
            RequestInfo requestInfo = await Execute(
                ValidateQueryMiddlewareTests.MiddlewareWithLegacyDocumentIdOrdering(),
                ("pageToken", ValidToken),
                ("maxChangeVersion", "200")
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        [Test]
        public async Task It_rejects_a_windowed_token_under_legacy_ordering()
        {
            AssertParameterValidationShell(
                await Execute(
                    ValidateQueryMiddlewareTests.MiddlewareWithLegacyDocumentIdOrdering(),
                    ("pageToken", WindowedToken),
                    ("maxChangeVersion", "200")
                ),
                CursorRequestValidator.InvalidPageToken
            );
        }

        [Test]
        public async Task It_leaves_collection_paging_unapplied_for_a_mismatched_token()
        {
            RequestInfo requestInfo = await Execute(true, ("pageToken", WindowedToken));

            requestInfo
                .CollectionPaging.Should()
                .Be(No.CollectionPaging, "a rejected request must not leave partially applied paging behind");
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

    /// <summary>
    /// What a request this step answers contributes to the collection-paging metric.
    /// </summary>
    /// <remarks>
    /// Every rejecting exit is covered here rather than split across the two GET-many test files,
    /// because this step answers a cursor fault, a traditional fault, a change-version fault, and a
    /// filter fault with the same measurement — and it is the difference between them, in one place,
    /// that shows the coverage is complete.
    /// </remarks>
    [TestFixture]
    [Parallelizable]
    public class Given_Collection_Paging_Telemetry_For_A_Rejection : ValidateQueryMiddlewareCursorTests
    {
        /// <summary>
        /// The live GET-many composition, recording what it counted. A mapping set is resolved onto the
        /// request because ResolveMappingSetMiddleware runs ahead of this step in that pipeline, so the
        /// provider a rejection reports is a real one.
        /// </summary>
        private static async Task<RecordingCollectionPagingTelemetry> ExecuteRecording(
            params (string Key, string Value)[] queryParameters
        )
        {
            RecordingCollectionPagingTelemetry telemetry = new();

            RequestInfo requestInfo = RequestInfoFor(EffectiveTargetKind.Primary, queryParameters);
            requestInfo.MappingSet = RelationalWriteSeamFixture
                .Create()
                .CreateSupportedMappingSet(SqlDialect.Pgsql);

            await ValidateQueryMiddlewareTests.Middleware(telemetry).Execute(requestInfo, NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);

            return telemetry;
        }

        // A rejected cursor request must report cursor. RequestInfo.CollectionPaging is assigned only at
        // the accepting exit, so reading paging mode from request state would report every rejected
        // cursor request as traditional traffic.
        [TestCase("!!!", TestName = "{m}(undecodable token)")]
        [TestCase("", TestName = "{m}(empty token)")]
        public async Task It_reports_a_rejected_cursor_request_as_cursor(string pageToken)
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteRecording(("pageToken", pageToken));

            CollectionPagingMeasurement measurement = telemetry.Single;

            measurement.Kind.Should().Be(CollectionPagingMeasurementKind.ValidationRejected);
            measurement.PagingMode.Should().Be("cursor");
            measurement.CommandCategory.Should().Be("none");
            measurement.Provider.Should().Be("postgresql");
            measurement.Outcome.Should().Be("validation_rejected");
        }

        // A request carrying pageSize alone is a cursor request too: it is rejected for naming no token,
        // and it must not be counted as traditional traffic either.
        [Test]
        public async Task It_reports_a_page_size_without_a_token_as_cursor()
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteRecording(("pageSize", "5"));

            telemetry.Single.PagingMode.Should().Be("cursor");
        }

        private static readonly TestCaseData[] _traditionalRejections =
        [
            new TestCaseData(new[] { ("limit", "-1") }).SetName("{m}(paging fault)"),
            new TestCaseData(new[] { ("minChangeVersion", "abc") }).SetName("{m}(change-version fault)"),
            new TestCaseData(new[] { ("notAField", "1") }).SetName("{m}(unknown query field)"),
            new TestCaseData(new[] { ("schoolId", "not-a-number") }).SetName("{m}(invalid filter value)"),
        ];

        // Every rejecting exit of this step counts, not only the paging ones: the ticket says
        // "validation rejection" without narrowing it to paging faults.
        [TestCaseSource(nameof(_traditionalRejections))]
        public async Task It_counts_every_rejecting_exit_exactly_once(
            (string Key, string Value)[] queryParameters
        )
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteRecording(queryParameters);

            CollectionPagingMeasurement measurement = telemetry.Single;

            measurement.Kind.Should().Be(CollectionPagingMeasurementKind.ValidationRejected);
            measurement.PagingMode.Should().Be("traditional");
            measurement.CommandCategory.Should().Be("none");
            measurement.Outcome.Should().Be("validation_rejected");
        }

        // Nothing executed, so a duration sample would report the cost of parsing a query string as a
        // read latency. The recording method that carries no duration is the one that must be called.
        [Test]
        public async Task It_records_no_duration_for_a_rejection()
        {
            RecordingCollectionPagingTelemetry telemetry = await ExecuteRecording(("limit", "-1"));

            telemetry.Single.Duration.Should().BeNull();
            telemetry.Single.Requested.Should().BeNull();
            telemetry.Single.Returned.Should().BeNull();
        }

        [Test]
        public async Task It_counts_nothing_for_a_request_it_accepts()
        {
            RecordingCollectionPagingTelemetry telemetry = new();
            RequestInfo requestInfo = RequestInfoFor(
                EffectiveTargetKind.Primary,
                ("schoolId", "1"),
                ("limit", "25")
            );

            await ValidateQueryMiddlewareTests.Middleware(telemetry).Execute(requestInfo, NullNext);

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            telemetry.Measurements.Should().BeEmpty();
        }

        // Counting runs ahead of the rejection this step answers with, so a measurement callback that
        // throws would replace a 400 naming the bad parameter with a system error naming nothing.
        [Test]
        public async Task It_still_answers_the_rejection_when_recording_throws()
        {
            RequestInfo requestInfo = RequestInfoFor(EffectiveTargetKind.Primary, ("limit", "-1"));

            await ValidateQueryMiddlewareTests
                .Middleware(new ThrowingCollectionPagingTelemetry())
                .Execute(requestInfo, NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            requestInfo
                .FrontendResponse.Body!.ToJsonString()
                .Should()
                .Contain("Limit must be omitted or set to a numeric value between 0 and");
        }
    }

    /// <summary>
    /// The token marker names the anchor a page was cut on, and a request resolves its anchor from the
    /// window <em>and</em> the data store serving it. A min-only walk therefore belongs to the source it
    /// started against: the same token, replayed with the same window against the other source, names
    /// bounds in the wrong units and is answered with the standard invalid-token response.
    /// </summary>
    /// <remarks>
    /// This is the pair that fails if the anchor is ever made to depend on paging shape — resolving one
    /// way for a traditional page and another for a cursor page would make a snapshot min-only page
    /// hand out a token its own follow-up request rejects, and every ordering test would still pass.
    /// </remarks>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Min_Only_Page_Token_Replayed_Against_A_Different_Data_Store
        : ValidateQueryMiddlewareCursorTests
    {
        [Test]
        public async Task It_accepts_a_content_version_token_against_a_snapshot()
        {
            RequestInfo requestInfo = await Execute(
                ValidateQueryMiddlewareTests.Middleware(),
                EffectiveTargetKind.Snapshot,
                ("pageToken", WindowedToken),
                ("minChangeVersion", "100")
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
            requestInfo
                .CollectionPaging.Should()
                .Be(new CollectionPaging.Cursor(new CursorRange(7, 42), new PageSize(MaximumPageSize)));
        }

        [Test]
        public async Task It_rejects_a_document_id_token_against_a_snapshot()
        {
            AssertParameterValidationShell(
                await Execute(
                    ValidateQueryMiddlewareTests.Middleware(),
                    EffectiveTargetKind.Snapshot,
                    ("pageToken", ValidToken),
                    ("minChangeVersion", "100")
                ),
                CursorRequestValidator.InvalidPageToken
            );
        }

        [Test]
        public async Task It_rejects_a_content_version_token_against_the_primary()
        {
            AssertParameterValidationShell(
                await Execute(
                    ValidateQueryMiddlewareTests.Middleware(),
                    EffectiveTargetKind.Primary,
                    ("pageToken", WindowedToken),
                    ("minChangeVersion", "100")
                ),
                CursorRequestValidator.InvalidPageToken
            );
        }

        /// <summary>
        /// The client-visible consequence, stated in one place: a min-only walk must keep its data
        /// source for its whole life, because adding or dropping the snapshot request mid-walk changes
        /// the order the collection is walked in exactly as changing the window would.
        /// </summary>
        [Test]
        public async Task It_answers_one_token_and_window_differently_by_data_store()
        {
            RequestInfo onTheSnapshot = await Execute(
                ValidateQueryMiddlewareTests.Middleware(),
                EffectiveTargetKind.Snapshot,
                ("pageToken", WindowedToken),
                ("minChangeVersion", "100")
            );
            RequestInfo onTheReplica = await Execute(
                ValidateQueryMiddlewareTests.Middleware(),
                EffectiveTargetKind.ReadReplica,
                ("pageToken", WindowedToken),
                ("minChangeVersion", "100")
            );

            onTheSnapshot.FrontendResponse.Should().Be(No.FrontendResponse);
            onTheReplica.FrontendResponse.StatusCode.Should().Be(400);
        }
    }
}
