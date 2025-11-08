// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO;
using System.Text;
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
public class ParseBodyMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new ParseBodyMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Null_Body : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_error_message_body()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("A non-empty request body is required");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Empty_Body : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: "",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_error_message_body()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("A non-empty request body is required");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Invalid_Json : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: """{ "id":"value" "name":"firstname"}""",
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId")
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _requestInfo?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_error_message_body()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Data validation failed.");
        }
    }

    [TestFixture]
    public class Given_A_Stream_Body : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var jsonBody = """{"name":"streamed"}""";
            var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(jsonBody));
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/stream",
                Body: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("trace"),
                BodyStream: stream
            );

            _requestInfo = new RequestInfo(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_requestInfo, () => Task.CompletedTask);
        }

        [Test]
        public void It_parses_body_and_clears_stream_reference()
        {
            _requestInfo.ParsedBody.Should().NotBeNull();
            _requestInfo.FrontendRequest.Body.Should().BeNull();
            _requestInfo.FrontendRequest.BodyStream.Should().BeNull();
        }
    }
}
