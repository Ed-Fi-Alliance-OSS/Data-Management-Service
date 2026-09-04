// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ValidateQueryMiddlewareTests
{
    private static readonly int _maxPageSize = 500;

    /// <summary>
    /// The live GET-many composition, which recognizes the cursor parameters.
    /// </summary>
    internal static IPipelineStep Middleware(ICollectionPagingTelemetry? collectionPagingTelemetry = null)
    {
        return new ValidateQueryMiddleware(
            NullLogger.Instance,
            _maxPageSize,
            _cursorParametersRecognized: true,
            collectionPagingTelemetry ?? NoOpCollectionPagingTelemetry.Instance,
            _useLegacyDocumentIdOrderingForChangeQueries: false
        );
    }

    /// <summary>
    /// The Change Query composition, which does not recognize the cursor parameters and does not count
    /// its faults as collection-paging traffic. Every argument is fixed here rather than defaulted, so
    /// this factory stays a faithful copy of how CreateGetTrackedChangesPipeline composes the step.
    /// </summary>
    internal static IPipelineStep MiddlewareWithoutCursorRecognition()
    {
        return new ValidateQueryMiddleware(
            NullLogger.Instance,
            _maxPageSize,
            _cursorParametersRecognized: false,
            NoOpCollectionPagingTelemetry.Instance,
            _useLegacyDocumentIdOrderingForChangeQueries: false
        );
    }

    /// <summary>
    /// The live GET-many composition of a deployment running with the page-ordering kill switch on.
    /// </summary>
    internal static IPipelineStep MiddlewareWithLegacyDocumentIdOrdering()
    {
        return new ValidateQueryMiddleware(
            NullLogger.Instance,
            _maxPageSize,
            _cursorParametersRecognized: true,
            NoOpCollectionPagingTelemetry.Instance,
            _useLegacyDocumentIdOrderingForChangeQueries: true
        );
    }

    /// <summary>
    /// The parameter-validation shell a pagination or change-version fault is answered with. Every
    /// dereference is hard on purpose: a null body or a missing key must fail the test rather than
    /// short-circuit the assertion that follows it. The reported messages are asserted separately by
    /// each fixture, which supplies its own faulty parameters.
    ///
    /// Every key of the shell is covered, including the two the shell carries empty, so a change
    /// that stopped emitting one of them cannot pass here and be caught only by the cursor fixtures.
    /// Every caller builds its request with an empty TraceId, which is what correlationId echoes.
    ///
    /// The media type is asserted alongside the body because nothing at the call site states it: it
    /// comes from the FrontendResponse default, and the frontend appends the charset that makes it
    /// the documented `application/json; charset=utf-8` response type.
    /// </summary>
    private static void AssertParameterValidationShell(RequestInfo requestInfo)
    {
        requestInfo.FrontendResponse.ContentType.Should().Be("application/json");

        JsonNode body = requestInfo.FrontendResponse.Body!;

        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:parameter-validation-failed");
        body["title"]!.GetValue<string>().Should().Be("Parameter Validation Failed");
        body["detail"]!.GetValue<string>().Should().Be("Parameters supplied to the request were invalid.");
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().BeEmpty();
        body["validationErrors"]!.AsObject().Should().BeEmpty();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Wrong_Query_Parameters : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var queryParameters = new Dictionary<string, string>
            {
                { "offset", "I" },
                { "limit", "-1" },
                { "totalCount", "100" },
            };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/schools",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.GET, ServiceProviderWithEffectiveTarget());
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_use_the_parameter_validation_failed_problem_details()
        {
            AssertParameterValidationShell(_requestInfo);
        }

        /// <summary>
        /// The pagination rules are evaluated together rather than exclusively, so all three faults
        /// are reported. Asserted as an ordered array rather than three substring checks because the
        /// order is a documented contract, and a substring check over the serialized body cannot see
        /// it. See change-queries.md, "Parameter Validation Failures".
        /// </summary>
        [Test]
        public void It_should_report_every_pagination_error_in_the_documented_order()
        {
            _requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal(
                    "Offset must be a numeric value greater than or equal to 0.",
                    $"Limit must be omitted or set to a numeric value between 0 and {_maxPageSize}.",
                    "TotalCount must be a boolean value."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Greater_Limit_Value : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            // Only limit is at fault. A second faulty parameter here would satisfy the shell
            // assertion below on its own, leaving the limit bound pinned by nothing but the
            // message check.
            var queryParameters = new Dictionary<string, string>
            {
                { "offset", "0" },
                { "limit", "800" },
                { "totalCount", "true" },
            };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/schools",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.GET, ServiceProviderWithEffectiveTarget());
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_use_the_parameter_validation_failed_problem_details()
        {
            AssertParameterValidationShell(_requestInfo);
        }

        /// <summary>
        /// The only fixture covering the upper bound, so it is asserted as the whole ordered array
        /// rather than a substring of the serialized body: cardinality is part of the contract here,
        /// and a spurious second entry alongside the expected message would satisfy a substring check.
        /// </summary>
        [Test]
        public void It_should_report_only_the_limit_error()
        {
            _requestInfo.FrontendResponse.Body!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal($"Limit must be omitted or set to a numeric value between 0 and {_maxPageSize}.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Invalid_Type_Query_Parameters : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        private static ApiSchemaDocuments NewApiSchemaDocuments()
        {
            var result = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("beginDate", [new("$.beginDate", "date")])
                .WithQueryField("schoolId", [new("$.schoolId", "number")])
                .WithQueryField("totalInstructionalDays", [new("$.totalInstructionalDays", "number")])
                .WithQueryField("isRequired", [new("$.isRequired", "boolean")])
                .WithQueryField("endDate", [new("$.endDate", "date-time")])
                .WithQueryField("classStartTime", [new("$.classStartTime", "time")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            return result;
        }

        private static RequestInfo NewRequestInfo(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo docRefContext = new(frontendRequest, method, ServiceProviderWithEffectiveTarget())
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    Operation: ResourcePathOperation.Collection.Instance
                ),
            };
            docRefContext.ProjectSchema =
                docRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            docRefContext.ResourceSchema = new ResourceSchema(
                docRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
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
                { "beginDate", "2024-30-12" },
                { "totalInstructionalDays", "Total" },
                { "schoolId", "School" },
                { "isRequired", "123" },
                { "endDate", "2025-12-30 33:00:00.000" },
                { "classStartTime", "44:80:99.123" },
            };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );

            _requestInfo = NewRequestInfo(frontendRequest, RequestMethod.GET);

            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_be_beginDate_error()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("$.beginDate");

            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("is not valid for beginDate.");
        }

        [Test]
        public void It_should_be_totalInstructionalDays_error()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("$.totalInstructionalDays");

            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("is not valid for totalInstructionalDays.");
        }

        [Test]
        public void It_should_be_SchoolId_error()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("$.schoolId");

            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("is not valid for schoolId.");
        }

        [Test]
        public void It_should_validate_boolean()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("$.isRequired");

            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("is not valid for isRequired.");
        }

        [Test]
        public void It_should_be_endDate_error()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("$.endDate");

            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("is not valid for endDate.");
        }

        [Test]
        public void It_should_be_time_error()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("$.classStartTime");

            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("is not valid for classStartTime.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Valid_Type_Query_Parameters : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        private static ApiSchemaDocuments NewApiSchemaDocuments()
        {
            var result = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("beginDate", [new("$.beginDate", "date")])
                .WithQueryField("schoolId", [new("$.schoolId", "number")])
                .WithQueryField("totalInstructionalDays", [new("$.totalInstructionalDays", "number")])
                .WithQueryField("isRequired", [new("$.isRequired", "boolean")])
                .WithQueryField("endDate", [new("$.endDate", "date-time")])
                .WithQueryField("classStartTime", [new("$.classStartTime", "time")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            return result;
        }

        private static RequestInfo NewRequestInfo(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo docRefContext = new(frontendRequest, method, ServiceProviderWithEffectiveTarget())
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    Operation: ResourcePathOperation.Collection.Instance
                ),
            };
            docRefContext.ProjectSchema =
                docRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            docRefContext.ResourceSchema = new ResourceSchema(
                docRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
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
                { "beginDate", "2025-01-01" },
                { "totalInstructionalDays", "123" },
                { "schoolId", "456" },
                { "isRequired", "true" },
                { "endDate", "2025-12-31" },
                { "classStartTime", "10:30:00" },
            };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );

            _requestInfo = NewRequestInfo(frontendRequest, RequestMethod.GET);

            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_provides_no_response()
        {
            _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Valid_Type_Query_Boolean_Parameter : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        private static ApiSchemaDocuments NewApiSchemaDocuments()
        {
            var result = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("isRequired", [new("$.isRequired", "boolean")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            return result;
        }

        private static RequestInfo NewRequestInfo(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo docRefContext = new(frontendRequest, method, ServiceProviderWithEffectiveTarget())
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    Operation: ResourcePathOperation.Collection.Instance
                ),
            };
            docRefContext.ProjectSchema =
                docRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            docRefContext.ResourceSchema = new ResourceSchema(
                docRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
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
            var queryParameters = new Dictionary<string, string> { { "isRequired", "false" } };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );

            _requestInfo = NewRequestInfo(frontendRequest, RequestMethod.GET);

            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_provides_no_response()
        {
            _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_Valid_Type_Query_DateTime_Parameter
        : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        private static ApiSchemaDocuments NewApiSchemaDocuments()
        {
            var result = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("beginDate", [new("$.beginDate", "date-time")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();

            return result;
        }

        private static RequestInfo NewRequestInfo(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo docRefContext = new(frontendRequest, method, ServiceProviderWithEffectiveTarget())
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    Operation: ResourcePathOperation.Collection.Instance
                ),
            };
            docRefContext.ProjectSchema =
                docRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            docRefContext.ResourceSchema = new ResourceSchema(
                docRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
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
                { "beginDate", "2025-12-30 22:33:55.000" },
            };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );

            _requestInfo = NewRequestInfo(frontendRequest, RequestMethod.GET);

            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_provides_no_response()
        {
            _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_A_Mixed_Case_Query_Field_Name : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        private static ApiSchemaDocuments NewApiSchemaDocuments()
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("schoolId", [new("$.schoolId", "number")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        private static RequestInfo NewRequestInfo(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo docRefContext = new(frontendRequest, method, ServiceProviderWithEffectiveTarget())
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    Operation: ResourcePathOperation.Collection.Instance
                ),
            };
            docRefContext.ProjectSchema =
                docRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            docRefContext.ResourceSchema = new ResourceSchema(
                docRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
            );

            return docRefContext;
        }

        [SetUp]
        public async Task Setup()
        {
            var queryParameters = new Dictionary<string, string> { { "SchoolId", "456" } };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );

            _requestInfo = NewRequestInfo(frontendRequest, RequestMethod.GET);

            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_accept_the_query_field()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_should_preserve_the_client_supplied_casing_on_the_query_element()
        {
            _requestInfo.QueryElements.Should().ContainSingle();

            _requestInfo
                .QueryElements.Single()
                .Should()
                .BeEquivalentTo(
                    new QueryElement(
                        QueryFieldName: "SchoolId",
                        DocumentPaths: [new JsonPath("$.schoolId")],
                        Value: "456",
                        Type: "number"
                    )
                );
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_Pipeline_Context_With_A_Query_Field_Name_Checked_Under_Turkish_Culture
        : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private CultureInfo _originalCurrentCulture = null!;
        private CultureInfo _originalCurrentUICulture = null!;

        private static ApiSchemaDocuments NewApiSchemaDocuments()
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("identifier", [new("$.identifier", "string")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        private static RequestInfo NewRequestInfo(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo docRefContext = new(frontendRequest, method, ServiceProviderWithEffectiveTarget())
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    Operation: ResourcePathOperation.Collection.Instance
                ),
            };
            docRefContext.ProjectSchema =
                docRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            docRefContext.ResourceSchema = new ResourceSchema(
                docRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
            );

            return docRefContext;
        }

        [SetUp]
        public async Task Setup()
        {
            _originalCurrentCulture = CultureInfo.CurrentCulture;
            _originalCurrentUICulture = CultureInfo.CurrentUICulture;

            var turkishCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = turkishCulture;
            CultureInfo.CurrentUICulture = turkishCulture;

            var queryParameters = new Dictionary<string, string> { { "IDENTIFIER", "456" } };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );

            _requestInfo = NewRequestInfo(frontendRequest, RequestMethod.GET);

            await Middleware().Execute(_requestInfo, NullNext);
        }

        [TearDown]
        public void TearDown()
        {
            CultureInfo.CurrentCulture = _originalCurrentCulture;
            CultureInfo.CurrentUICulture = _originalCurrentUICulture;
        }

        [Test]
        public void It_should_accept_the_query_field()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_should_preserve_the_client_supplied_casing_on_the_query_element()
        {
            _requestInfo.QueryElements.Should().ContainSingle();

            _requestInfo
                .QueryElements.Single()
                .Should()
                .BeEquivalentTo(
                    new QueryElement(
                        QueryFieldName: "IDENTIFIER",
                        DocumentPaths: [new JsonPath("$.identifier")],
                        Value: "456",
                        Type: "string"
                    )
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Pipeline_Context_With_An_Invalid_Query_Field_Name : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        private static ApiSchemaDocuments NewApiSchemaDocuments()
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("schoolId", [new("$.schoolId", "number")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        private static RequestInfo NewRequestInfo(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo docRefContext = new(frontendRequest, method, ServiceProviderWithEffectiveTarget())
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    Operation: ResourcePathOperation.Collection.Instance
                ),
            };
            docRefContext.ProjectSchema =
                docRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            docRefContext.ResourceSchema = new ResourceSchema(
                docRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
            );

            return docRefContext;
        }

        [SetUp]
        public async Task Setup()
        {
            var queryParameters = new Dictionary<string, string> { { "invalidSchoolId", "456" } };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );

            _requestInfo = NewRequestInfo(frontendRequest, RequestMethod.GET);

            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_report_the_existing_invalid_query_field_error()
        {
            _requestInfo.FrontendResponse.Body!["errors"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The query field 'invalidSchoolId' is not valid for this resource.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Invalid_Min_Change_Version : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var queryParameters = new Dictionary<string, string> { { "minChangeVersion", "abc" } };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/schools",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.GET, ServiceProviderWithEffectiveTarget());
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_use_the_parameter_validation_failed_problem_details()
        {
            AssertParameterValidationShell(_requestInfo);
        }

        [Test]
        public void It_should_report_the_min_change_version_error()
        {
            _requestInfo
                .FrontendResponse.Body?["errors"]?[0]?.GetValue<string>()
                .Should()
                .Be("MinChangeVersion must be a numeric value greater than or equal to 0.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Invalid_Max_Change_Version : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var queryParameters = new Dictionary<string, string> { { "maxChangeVersion", "-2" } };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/schools",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.GET, ServiceProviderWithEffectiveTarget());
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_use_the_parameter_validation_failed_problem_details()
        {
            AssertParameterValidationShell(_requestInfo);
        }

        [Test]
        public void It_should_report_the_max_change_version_error()
        {
            _requestInfo
                .FrontendResponse.Body?["errors"]?[0]?.GetValue<string>()
                .Should()
                .Be("MaxChangeVersion must be a numeric value greater than or equal to 0.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Inverted_Change_Version_Range : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var queryParameters = new Dictionary<string, string>
            {
                { "minChangeVersion", "10" },
                { "maxChangeVersion", "5" },
            };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/schools",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );
            _requestInfo = new(frontendRequest, RequestMethod.GET, ServiceProviderWithEffectiveTarget());
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_use_the_parameter_validation_failed_problem_details()
        {
            AssertParameterValidationShell(_requestInfo);
        }

        [Test]
        public void It_should_report_the_inverted_range_error()
        {
            _requestInfo
                .FrontendResponse.Body?["errors"]?[0]?.GetValue<string>()
                .Should()
                .Be("MinChangeVersion must be less than or equal to MaxChangeVersion.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Change_Version_Range : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        private static ApiSchemaDocuments NewApiSchemaDocuments()
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("schoolId", [new("$.schoolId", "number")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        private static RequestInfo NewRequestInfo(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo docRefContext = new(frontendRequest, method, ServiceProviderWithEffectiveTarget())
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    Operation: ResourcePathOperation.Collection.Instance
                ),
            };
            docRefContext.ProjectSchema =
                docRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            docRefContext.ResourceSchema = new ResourceSchema(
                docRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
            );
            return docRefContext;
        }

        [SetUp]
        public async Task Setup()
        {
            var queryParameters = new Dictionary<string, string>
            {
                { "minChangeVersion", "1" },
                { "maxChangeVersion", "2" },
            };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );

            _requestInfo = NewRequestInfo(frontendRequest, RequestMethod.GET);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_provides_no_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public void It_sets_the_parsed_change_version_range()
        {
            _requestInfo.ChangeVersionRange.Should().Be(new ChangeVersionRange(1, 2));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Change_Version_Parameters_Are_Not_Treated_As_Query_Fields
        : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        private static ApiSchemaDocuments NewApiSchemaDocuments()
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("schoolId", [new("$.schoolId", "number")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        private static RequestInfo NewRequestInfo(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo docRefContext = new(frontendRequest, method, ServiceProviderWithEffectiveTarget())
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    Operation: ResourcePathOperation.Collection.Instance
                ),
            };
            docRefContext.ProjectSchema =
                docRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            docRefContext.ResourceSchema = new ResourceSchema(
                docRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
            );
            return docRefContext;
        }

        [SetUp]
        public async Task Setup()
        {
            // Mixed casing confirms the reserved-parameter exclusion is case-insensitive.
            var queryParameters = new Dictionary<string, string>
            {
                { "MinChangeVersion", "1" },
                { "maxChangeVersion", "2" },
            };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );

            _requestInfo = NewRequestInfo(frontendRequest, RequestMethod.GET);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_not_report_an_invalid_query_field()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Mixed_Case_Pagination_Parameter : ValidateQueryMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        private static ApiSchemaDocuments NewApiSchemaDocuments()
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("schoolId", [new("$.schoolId", "number")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        private static RequestInfo NewRequestInfo(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo docRefContext = new(frontendRequest, method, ServiceProviderWithEffectiveTarget())
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    Operation: ResourcePathOperation.Collection.Instance
                ),
            };
            docRefContext.ProjectSchema =
                docRefContext.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))!;
            docRefContext.ResourceSchema = new ResourceSchema(
                docRefContext.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicWeeks"))
                    ?? new JsonObject()
            );
            return docRefContext;
        }

        [SetUp]
        public async Task Setup()
        {
            // A pagination parameter in non-canonical casing is not parsed as pagination and
            // must not be silently dropped; it falls through to ordinary query-field matching.
            var queryParameters = new Dictionary<string, string> { { "Limit", "-1" } };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/academicWeeks",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId(""),
                RouteQualifiers: []
            );

            _requestInfo = NewRequestInfo(frontendRequest, RequestMethod.GET);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_report_the_invalid_query_field()
        {
            _requestInfo.FrontendResponse.Body!["errors"]![0]!
                .GetValue<string>()
                .Should()
                .Be("The query field 'Limit' is not valid for this resource.");
        }
    }

    /// <summary>
    /// The page anchor this step resolves: the ordering key a page's bounds and its continuation
    /// token are expressed in. A max-bearing window (max-only or min+max) anchors on ContentVersion
    /// because an update pushes a row past the maximum and out of the window entirely; every other
    /// window shape keeps DocumentId, because an update inside an open-ended window moves a row past
    /// a ContentVersion anchor while it is still eligible and a walk would return it twice.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_Whose_Page_Anchor_Is_Resolved : ValidateQueryMiddlewareTests
    {
        private static ApiSchemaDocuments NewApiSchemaDocuments()
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("schoolId", [new("$.schoolId", "number")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        private static RequestInfo NewRequestInfo(
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
                TraceId: new TraceId(""),
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

        /// <summary>
        /// Executes against the primary, which is where every expectation written before the data
        /// source mattered belongs.
        /// </summary>
        private static Task<RequestInfo> Execute(
            IPipelineStep middleware,
            params (string Key, string Value)[] queryParameters
        )
        {
            return Execute(middleware, EffectiveTargetKind.Primary, queryParameters);
        }

        private static async Task<RequestInfo> Execute(
            IPipelineStep middleware,
            EffectiveTargetKind targetKind,
            params (string Key, string Value)[] queryParameters
        )
        {
            RequestInfo requestInfo = NewRequestInfo(targetKind, queryParameters);

            await middleware.Execute(requestInfo, NullNext);

            return requestInfo;
        }

        [Test]
        public async Task It_anchors_a_max_only_window_on_the_content_version()
        {
            RequestInfo requestInfo = await Execute(Middleware(), ("maxChangeVersion", "200"));

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
        }

        [Test]
        public async Task It_anchors_a_min_and_max_window_on_the_content_version()
        {
            RequestInfo requestInfo = await Execute(
                Middleware(),
                ("minChangeVersion", "100"),
                ("maxChangeVersion", "200")
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
        }

        [Test]
        public async Task It_anchors_a_min_only_window_on_the_document_id()
        {
            RequestInfo requestInfo = await Execute(Middleware(), ("minChangeVersion", "100"));

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        [Test]
        public async Task It_anchors_an_unfiltered_request_on_the_document_id()
        {
            RequestInfo requestInfo = await Execute(Middleware(), ("schoolId", "255901"));

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        /// <summary>
        /// A present-but-empty maximum parses to null, so the window is not max-bearing. Resolving
        /// from parameter presence instead of from the parsed window would anchor this request on
        /// ContentVersion and issue a token no unwindowed continuation could replay.
        /// </summary>
        [TestCase("", TestName = "empty")]
        [TestCase("   ", TestName = "whitespace")]
        public async Task It_anchors_a_present_but_blank_maximum_on_the_document_id(string maximum)
        {
            RequestInfo requestInfo = await Execute(Middleware(), ("maxChangeVersion", maximum));

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        /// <summary>
        /// An unparseable maximum is rejected, and the anchor it would have resolved to is DocumentId
        /// rather than ContentVersion: the request never carries an anchor a handler could act on, and
        /// the resolution does not depend on the faulty value.
        /// </summary>
        [TestCase("abc", TestName = "not a number")]
        [TestCase("-2", TestName = "negative")]
        public async Task It_rejects_an_unparseable_maximum_without_moving_the_anchor(string maximum)
        {
            RequestInfo requestInfo = await Execute(Middleware(), ("maxChangeVersion", maximum));

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        /// <summary>
        /// The anchor is a function of the window, so the two are applied together: a request never
        /// carries a window without the anchor it resolves to, nor an anchor without its window.
        /// Asserted on a request the query-field phase rejects, which is the one outcome that reaches
        /// state past their shared assignment site and still answers 400.
        /// </summary>
        [Test]
        public async Task It_applies_the_anchor_wherever_it_applies_the_window()
        {
            RequestInfo rejectedLater = await Execute(
                Middleware(),
                ("maxChangeVersion", "200"),
                ("notAQueryField", "1")
            );

            rejectedLater
                .FrontendResponse.StatusCode.Should()
                .Be(400, "the arrangement must reach a rejection after the anchor was resolved");
            rejectedLater.ChangeVersionRange.Should().Be(new ChangeVersionRange(null, 200));
            rejectedLater.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);

            RequestInfo rejectedByTheWindow = await Execute(Middleware(), ("maxChangeVersion", "abc"));

            rejectedByTheWindow.ChangeVersionRange.Should().Be(ChangeVersionRange.None);
            rejectedByTheWindow.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        /// <summary>
        /// The kill switch restores DocumentId ordering for every window shape, which is what keeps
        /// the tokens a legacy deployment issues replayable against it.
        /// </summary>
        [Test]
        public async Task It_anchors_every_window_on_the_document_id_under_legacy_ordering()
        {
            RequestInfo maxOnly = await Execute(
                MiddlewareWithLegacyDocumentIdOrdering(),
                ("maxChangeVersion", "200")
            );
            RequestInfo minAndMax = await Execute(
                MiddlewareWithLegacyDocumentIdOrdering(),
                ("minChangeVersion", "100"),
                ("maxChangeVersion", "200")
            );

            maxOnly.FrontendResponse.Should().Be(No.FrontendResponse);
            maxOnly.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
            minAndMax.FrontendResponse.Should().Be(No.FrontendResponse);
            minAndMax.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        /// <summary>
        /// The Change Query composition resolves the anchor by the same rule, so one composition
        /// cannot answer a windowed request with an ordering the others would not.
        /// </summary>
        [Test]
        public async Task It_resolves_the_same_anchor_without_cursor_recognition()
        {
            RequestInfo requestInfo = await Execute(
                MiddlewareWithoutCursorRecognition(),
                ("maxChangeVersion", "200")
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
        }

        /// <summary>
        /// The window no longer decides the anchor on its own. A min-only window served from a frozen
        /// snapshot anchors on ContentVersion, because nothing in a frozen source can move a row later
        /// within the still-open window and take the duplicate-and-skip hazard with it.
        /// </summary>
        [Test]
        public async Task It_anchors_a_min_only_window_on_the_content_version_against_a_snapshot()
        {
            RequestInfo requestInfo = await Execute(
                Middleware(),
                EffectiveTargetKind.Snapshot,
                ("minChangeVersion", "100")
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
        }

        /// <summary>
        /// The shapes that already anchored on ContentVersion are unchanged by the data source: the
        /// snapshot rule widens which windows take that anchor, it does not narrow any.
        /// </summary>
        [Test]
        public async Task It_anchors_the_max_bearing_windows_on_the_content_version_against_a_snapshot()
        {
            RequestInfo maxOnly = await Execute(
                Middleware(),
                EffectiveTargetKind.Snapshot,
                ("maxChangeVersion", "200")
            );
            RequestInfo minAndMax = await Execute(
                Middleware(),
                EffectiveTargetKind.Snapshot,
                ("minChangeVersion", "100"),
                ("maxChangeVersion", "200")
            );

            maxOnly.FrontendResponse.Should().Be(No.FrontendResponse);
            maxOnly.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
            minAndMax.FrontendResponse.Should().Be(No.FrontendResponse);
            minAndMax.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
        }

        /// <summary>
        /// An unfiltered read keeps DocumentId even on a snapshot. With no window predicate there is
        /// no planner pathology to fix and nothing to gain, and routing a request to a snapshot must
        /// not by itself change the order a collection is walked in.
        /// </summary>
        [Test]
        public async Task It_anchors_an_unfiltered_snapshot_request_on_the_document_id()
        {
            RequestInfo requestInfo = await Execute(
                Middleware(),
                EffectiveTargetKind.Snapshot,
                ("schoolId", "255901")
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        /// <summary>
        /// The snapshot rule reads the parsed window like the live one does, so a bound that is
        /// present but does not parse is an absent bound on either data source.
        /// </summary>
        [TestCase("", TestName = "snapshot, empty maximum")]
        [TestCase("   ", TestName = "snapshot, whitespace maximum")]
        public async Task It_anchors_a_present_but_blank_snapshot_maximum_on_the_document_id(string maximum)
        {
            RequestInfo requestInfo = await Execute(
                Middleware(),
                EffectiveTargetKind.Snapshot,
                ("maxChangeVersion", maximum)
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        /// <summary>
        /// A window this step is about to reject resolves no anchor at all, so an incoming token is
        /// never compared against one. The client is told what is actually wrong - the maximum it
        /// typed - and keeps the one piece of state it cannot rebuild.
        /// </summary>
        /// <remarks>
        /// Asserting the reported message is what makes this able to fail, and the ContentVersion
        /// token is what gives it something to report. The resolved anchor itself is not observable
        /// on a rejected request: PageOrderingMode is assigned only at the accepting exit, so it
        /// still carries its DocumentId default here whatever the snapshot rule returned - which is
        /// why asserting that default proves nothing. A regression that resolved an anchor from the
        /// faulty window would resolve DocumentId from its surviving bounds, disagree with this
        /// token, and answer with the invalid-token message instead of the one asserted here.
        /// </remarks>
        [TestCase("abc", TestName = "snapshot, maximum is not a number")]
        [TestCase("-2", TestName = "snapshot, negative maximum")]
        public async Task It_reports_an_unparseable_snapshot_maximum_rather_than_the_token(string maximum)
        {
            RequestInfo requestInfo = await Execute(
                Middleware(),
                EffectiveTargetKind.Snapshot,
                ("pageToken", PageTokenCodec.Encode(new CursorRange(7, 42), PageOrderingMode.ContentVersion)),
                ("maxChangeVersion", maximum)
            );

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            requestInfo
                .FrontendResponse.Body?["errors"]?[0]?.GetValue<string>()
                .Should()
                .Be("MaxChangeVersion must be a numeric value greater than or equal to 0.");
        }

        /// <summary>
        /// Only a frozen source qualifies. A read replica keeps applying changes, so a row can still
        /// move later within an open window there and the live rule stands — which is what stops a
        /// later simplification to "anything that is not the primary" from passing.
        /// </summary>
        [TestCase(EffectiveTargetKind.Primary, TestName = "primary keeps the live rule")]
        [TestCase(EffectiveTargetKind.ReadReplica, TestName = "read replica keeps the live rule")]
        public async Task It_anchors_a_min_only_window_on_the_document_id_for_every_unfrozen_target(
            EffectiveTargetKind targetKind
        )
        {
            RequestInfo requestInfo = await Execute(Middleware(), targetKind, ("minChangeVersion", "100"));

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        /// <summary>
        /// The kill switch overrides every branch, the snapshot one included, so a legacy deployment
        /// issues DocumentId tokens whichever database served the request.
        /// </summary>
        [Test]
        public async Task It_anchors_a_snapshot_window_on_the_document_id_under_legacy_ordering()
        {
            RequestInfo minOnly = await Execute(
                MiddlewareWithLegacyDocumentIdOrdering(),
                EffectiveTargetKind.Snapshot,
                ("minChangeVersion", "100")
            );
            RequestInfo maxOnly = await Execute(
                MiddlewareWithLegacyDocumentIdOrdering(),
                EffectiveTargetKind.Snapshot,
                ("maxChangeVersion", "200")
            );

            minOnly.FrontendResponse.Should().Be(No.FrontendResponse);
            minOnly.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
            maxOnly.FrontendResponse.Should().Be(No.FrontendResponse);
            maxOnly.PageOrderingMode.Should().Be(PageOrderingMode.DocumentId);
        }

        /// <summary>
        /// The Change Query composition resolves by the target too. Nothing on that pipeline reads the
        /// anchor, so this is inert by design — and asserted so that the rule stays in one place
        /// rather than acquiring a second, composition-specific form.
        /// </summary>
        [Test]
        public async Task It_resolves_by_the_target_without_cursor_recognition()
        {
            RequestInfo requestInfo = await Execute(
                MiddlewareWithoutCursorRecognition(),
                EffectiveTargetKind.Snapshot,
                ("minChangeVersion", "100")
            );

            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            requestInfo.PageOrderingMode.Should().Be(PageOrderingMode.ContentVersion);
        }
    }
}
