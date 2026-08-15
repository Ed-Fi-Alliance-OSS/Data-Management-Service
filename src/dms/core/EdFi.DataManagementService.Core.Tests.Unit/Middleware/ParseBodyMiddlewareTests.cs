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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
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
    public class Given_A_Post_Request_With_Whitespace_Only_Body : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: " \r\n\t ",
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
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
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
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
    [Parallelizable]
    public class Given_A_Post_Request_With_Preparsed_Json : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private JsonNode _parsedBody = new JsonObject();
        private bool _nextWasCalled;

        [SetUp]
        public async Task Setup()
        {
            _parsedBody = JsonNode.Parse("""{"id":"value","name":"firstname"}""")!;

            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: [],
                ParsedBody: _parsedBody
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware()
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextWasCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_sets_the_parsed_body()
        {
            _requestInfo.ParsedBody.Should().BeSameAs(_parsedBody);
        }

        [Test]
        public void It_continues_the_pipeline()
        {
            _nextWasCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_set_an_error_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_With_Frontend_Parse_Error : ParseBodyMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var frontEndRequest = new FrontendRequest(
                Path: "ed-fi/schools",
                Body: null,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("correlation-id"),
                RouteQualifiers: [],
                BodyParseErrorMessage: "'n' is invalid after a value."
            );
            _requestInfo = new(frontEndRequest, RequestMethod.POST, No.ServiceProvider);
            await Middleware().Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_data_validation_error_message_body()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Data validation failed.")
                .And.Contain("invalid after a value.");
        }

        [Test]
        public void It_preserves_the_correlation_id()
        {
            _requestInfo
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("\"correlationId\":\"correlation-id\"");
        }
    }

    /// <summary>
    /// The middleware owns body parsing only. A failure raised further down the pipeline is not a
    /// client validation error, and its message is internal, so the exception must travel on to the
    /// pipeline's exception handler rather than being answered here.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Downstream_Step_Throws : ParseBodyMiddlewareTests
    {
        private const string DownstreamMessage =
            "Transaction (Process ID 112) was deadlocked on lock resources with another process "
            + "and has been chosen as the deadlock victim. Rerun the transaction.";

        private static RequestInfo RequestWith(JsonNode? parsedBody, string? body) =>
            new(
                new FrontendRequest(
                    Path: "ed-fi/schools",
                    Body: body,
                    Form: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: [],
                    ParsedBody: parsedBody
                ),
                RequestMethod.POST,
                No.ServiceProvider
            );

        private static Func<Task> ThrowingNext =>
            () => throw new InvalidOperationException(DownstreamMessage);

        [Test]
        public async Task It_propagates_the_exception_when_the_frontend_pre_parsed_the_body()
        {
            var requestInfo = RequestWith(JsonNode.Parse("""{ "schoolId": 1 }"""), body: null);

            Func<Task> act = () => Middleware().Execute(requestInfo, ThrowingNext);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(DownstreamMessage);
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }

        [Test]
        public async Task It_propagates_the_exception_when_the_middleware_parsed_the_body()
        {
            var requestInfo = RequestWith(parsedBody: null, body: """{ "schoolId": 1 }""");

            Func<Task> act = () => Middleware().Execute(requestInfo, ThrowingNext);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(DownstreamMessage);
            requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
