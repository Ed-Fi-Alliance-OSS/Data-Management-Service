// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
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
[Parallelizable]
public class ValidateQueryMiddlewareTests
{
    private static readonly int _maxPageSize = 500;

    /// <summary>
    /// The live GET-many composition, which recognizes the cursor parameters.
    /// </summary>
    internal static IPipelineStep Middleware()
    {
        return new ValidateQueryMiddleware(
            NullLogger.Instance,
            _maxPageSize,
            _cursorParametersRecognized: true
        );
    }

    /// <summary>
    /// The Change Query composition, which does not recognize the cursor parameters.
    /// </summary>
    internal static IPipelineStep MiddlewareWithoutCursorRecognition()
    {
        return new ValidateQueryMiddleware(
            NullLogger.Instance,
            _maxPageSize,
            _cursorParametersRecognized: false
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
    /// </summary>
    private static void AssertParameterValidationShell(RequestInfo requestInfo)
    {
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
            _requestInfo = new(frontendRequest, RequestMethod.GET, No.ServiceProvider);
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
            _requestInfo = new(frontendRequest, RequestMethod.GET, No.ServiceProvider);
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
            RequestInfo docRefContext = new(frontendRequest, method, No.ServiceProvider)
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
            RequestInfo docRefContext = new(frontendRequest, method, No.ServiceProvider)
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
            RequestInfo docRefContext = new(frontendRequest, method, No.ServiceProvider)
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
            RequestInfo docRefContext = new(frontendRequest, method, No.ServiceProvider)
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
            RequestInfo docRefContext = new(frontendRequest, method, No.ServiceProvider)
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
            RequestInfo docRefContext = new(frontendRequest, method, No.ServiceProvider)
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
            RequestInfo docRefContext = new(frontendRequest, method, No.ServiceProvider)
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
            _requestInfo = new(frontendRequest, RequestMethod.GET, No.ServiceProvider);
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
            _requestInfo = new(frontendRequest, RequestMethod.GET, No.ServiceProvider);
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
            _requestInfo = new(frontendRequest, RequestMethod.GET, No.ServiceProvider);
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
            RequestInfo docRefContext = new(frontendRequest, method, No.ServiceProvider)
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
            RequestInfo docRefContext = new(frontendRequest, method, No.ServiceProvider)
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
            RequestInfo docRefContext = new(frontendRequest, method, No.ServiceProvider)
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
}
