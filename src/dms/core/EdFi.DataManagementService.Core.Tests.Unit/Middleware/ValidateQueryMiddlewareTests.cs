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
[Parallelizable]
public class ValidateQueryMiddlewareTests
{
    private static readonly int _maxPageSize = 500;

    internal static IPipelineStep Middleware()
    {
        return new ValidateQueryMiddleware(NullLogger.Instance, _maxPageSize);
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
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId("")
            );
            _requestInfo = new(frontendRequest, RequestMethod.GET);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_be_errors()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("The request could not be processed.");
        }

        [Test]
        public void It_should_be_offset_errors()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Offset must be a numeric value greater than or equal to 0.");
        }

        [Test]
        public void It_should_be_limit_errors()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain($"Limit must be omitted or set to a numeric value between 0 and {_maxPageSize}.");
        }

        [Test]
        public void It_should_be_total_count_errors()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("TotalCount must be a boolean value.");
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
            var queryParameters = new Dictionary<string, string>
            {
                { "offset", "0" },
                { "limit", "800" },
                { "totalCount", "100" },
            };

            FrontendRequest frontendRequest = new(
                Path: "/ed-fi/schools",
                Body: null,
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId("")
            );
            _requestInfo = new(frontendRequest, RequestMethod.GET);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_be_errors()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("The request could not be processed.");
        }

        [Test]
        public void It_should_be_limit_errors()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain($"Limit must be omitted or set to a numeric value between 0 and {_maxPageSize}.");
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
            RequestInfo docRefContext = new(frontendRequest, method)
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    DocumentUuid: No.DocumentUuid
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
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId("")
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
            RequestInfo docRefContext = new(frontendRequest, method)
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    DocumentUuid: No.DocumentUuid
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
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId("")
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
            RequestInfo docRefContext = new(frontendRequest, method)
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    DocumentUuid: No.DocumentUuid
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
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId("")
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
            RequestInfo docRefContext = new(frontendRequest, method)
            {
                ApiSchemaDocuments = NewApiSchemaDocuments(),
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("academicWeeks"),
                    DocumentUuid: No.DocumentUuid
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
                Headers: [],
                QueryParameters: queryParameters,
                TraceId: new TraceId("")
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
}
