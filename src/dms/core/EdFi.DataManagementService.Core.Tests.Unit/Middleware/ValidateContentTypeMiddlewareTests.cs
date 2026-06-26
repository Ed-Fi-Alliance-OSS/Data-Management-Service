// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ValidateContentTypeMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new ValidateContentTypeMiddleware(NullLogger.Instance);
    }

    private static RequestInfo RequestInfoWith(string? contentType, RequestMethod method = RequestMethod.POST)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (contentType is not null)
        {
            headers["Content-Type"] = contentType;
        }

        var frontendRequest = new FrontendRequest(
            Path: "ed-fi/schools",
            Body: """{ "schoolId": 1 }""",
            Form: null,
            Headers: headers,
            QueryParameters: [],
            TraceId: new TraceId("traceId"),
            RouteQualifiers: []
        );

        return new(frontendRequest, method, No.ServiceProvider);
    }

    /// <summary>
    /// A next delegate that records whether the pipeline continued past the middleware.
    /// </summary>
    private sealed class CountingNext
    {
        public bool WasCalled { get; private set; }

        public Func<Task> Next =>
            () =>
            {
                WasCalled = true;
                return Task.CompletedTask;
            };
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Unsupported_Content_Type : ValidateContentTypeMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly CountingNext _next = new();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = RequestInfoWith("text/plain");
            await Middleware().Execute(_requestInfo, _next.Next);
        }

        [Test]
        public void It_does_not_continue_the_pipeline()
        {
            _next.WasCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_status_415()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(415);
        }

        [Test]
        public void It_returns_problem_json_content_type()
        {
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_returns_the_unsupported_media_type_problem_details()
        {
            JsonObject body = _requestInfo.FrontendResponse.Body!.AsObject();

            body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:unsupported-media-type");
            body["title"]?.GetValue<string>().Should().Be("Unsupported Media Type");
            body["status"]?.GetValue<int>().Should().Be(415);
            body["detail"]
                ?.GetValue<string>()
                .Should()
                .Be("The value specified in the 'Content-Type' header is not supported by this host.");
            body["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be("The value specified in the 'Content-Type' header is not supported by this host.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Put_Request_With_Unsupported_Content_Type : ValidateContentTypeMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly CountingNext _next = new();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = RequestInfoWith("application/xml", RequestMethod.PUT);
            await Middleware().Execute(_requestInfo, _next.Next);
        }

        [Test]
        public void It_does_not_continue_the_pipeline()
        {
            _next.WasCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_status_415()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(415);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_An_Unparseable_Content_Type : ValidateContentTypeMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly CountingNext _next = new();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = RequestInfoWith("not-a-media-type");
            await Middleware().Execute(_requestInfo, _next.Next);
        }

        [Test]
        public void It_does_not_continue_the_pipeline()
        {
            _next.WasCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_status_415()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(415);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Application_Json : ValidateContentTypeMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly CountingNext _next = new();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = RequestInfoWith("application/json");
            await Middleware().Execute(_requestInfo, _next.Next);
        }

        [Test]
        public void It_continues_the_pipeline()
        {
            _next.WasCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_set_a_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Application_Json_And_Charset : ValidateContentTypeMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly CountingNext _next = new();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = RequestInfoWith("application/json; charset=utf-8");
            await Middleware().Execute(_requestInfo, _next.Next);
        }

        [Test]
        public void It_continues_the_pipeline()
        {
            _next.WasCalled.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Text_Json : ValidateContentTypeMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly CountingNext _next = new();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = RequestInfoWith("text/json");
            await Middleware().Execute(_requestInfo, _next.Next);
        }

        [Test]
        public void It_continues_the_pipeline()
        {
            _next.WasCalled.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_A_Profile_Writable_Content_Type
        : ValidateContentTypeMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly CountingNext _next = new();

        [SetUp]
        public async Task Setup()
        {
            // Ed-Fi profile media types are deferred to ProfileResolutionMiddleware, not rejected here.
            _requestInfo = RequestInfoWith("application/vnd.ed-fi.school.test-profile.writable+json");
            await Middleware().Execute(_requestInfo, _next.Next);
        }

        [Test]
        public void It_continues_the_pipeline()
        {
            _next.WasCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_set_a_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_No_Content_Type : ValidateContentTypeMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private readonly CountingNext _next = new();

        [SetUp]
        public async Task Setup()
        {
            // A missing Content-Type is not an explicit unsupported value and must not be rejected.
            _requestInfo = RequestInfoWith(null);
            await Middleware().Execute(_requestInfo, _next.Next);
        }

        [Test]
        public void It_continues_the_pipeline()
        {
            _next.WasCalled.Should().BeTrue();
        }
    }
}
