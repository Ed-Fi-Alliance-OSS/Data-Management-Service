// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Polly.CircuitBreaker;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class CoreExceptionLoggingMiddlewareTests
{
    [Test]
    public async Task It_propagates_request_cancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();
        var requestInfo = No.RequestInfo("traceId");
        requestInfo.RequestCancellationToken = cancellationSource.Token;
        var middleware = new CoreExceptionLoggingMiddleware(NullLogger.Instance);

        Func<Task> act = async () =>
            await middleware.Execute(
                requestInfo,
                () => throw new OperationCanceledException(cancellationSource.Token)
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
        requestInfo.CaughtException.Should().BeNull();
        requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
    }

    /// <summary>
    /// An open circuit means the backend is shedding load, not that the request was wrong. It has to
    /// read as retriable so a client replays it rather than dropping the document, and the break
    /// duration is the honest retry hint.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Circuit_Is_Open : CoreExceptionLoggingMiddlewareTests
    {
        private static async Task<IFrontendResponse> ExecuteWith(TimeSpan? breakDuration)
        {
            var requestInfo = No.RequestInfo("traceId");
            var middleware = new CoreExceptionLoggingMiddleware(NullLogger.Instance, breakDuration);

            await middleware.Execute(
                requestInfo,
                () => throw new BrokenCircuitException("The circuit is now open and is not allowing calls.")
            );

            return requestInfo.FrontendResponse;
        }

        [Test]
        public async Task It_returns_503_problem_details()
        {
            var response = await ExecuteWith(TimeSpan.FromSeconds(30));

            response.StatusCode.Should().Be(503);
            response.ContentType.Should().Be("application/problem+json");

            JsonObject body = response.Body!.AsObject();
            body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:service-unavailable");
            body["status"]?.GetValue<int>().Should().Be(503);
            body["correlationId"]?.GetValue<string>().Should().Be("traceId");
        }

        [Test]
        public async Task It_does_not_disclose_the_internal_message()
        {
            var response = await ExecuteWith(TimeSpan.FromSeconds(30));

            response.Body!.ToJsonString().Should().NotContain("circuit");
        }

        [Test]
        public async Task It_serves_retry_after_from_the_configured_break_duration()
        {
            var response = await ExecuteWith(TimeSpan.FromSeconds(30));

            response.Headers.Should().ContainKey("Retry-After");
            response.Headers["Retry-After"].Should().Be("30");
        }

        [Test]
        public async Task It_rounds_a_fractional_break_duration_up_to_whole_seconds()
        {
            var response = await ExecuteWith(TimeSpan.FromMilliseconds(1500));

            response.Headers["Retry-After"].Should().Be("2");
        }

        [Test]
        public async Task It_omits_retry_after_when_no_break_duration_is_configured()
        {
            var response = await ExecuteWith(breakDuration: null);

            response.Headers.Should().NotContainKey("Retry-After");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Unhandled_Exception : CoreExceptionLoggingMiddlewareTests
    {
        private FrontendResponse _response = null!;

        private static void AssertExpectedServerErrorResponse(
            IFrontendResponse response,
            string expectedMessage,
            string expectedTraceId
        )
        {
            response.StatusCode.Should().Be(500);
            response.ContentType.Should().Be("application/json");

            JsonObject body = response.Body!.AsObject();

            body.Select(property => property.Key).Should().BeEquivalentTo("message", "traceId");
            body["message"]?.GetValue<string>().Should().Be(expectedMessage);
            body["traceId"]?.GetValue<string>().Should().Be(expectedTraceId);
            body["detail"].Should().BeNull();
            body["type"].Should().BeNull();
            body["title"].Should().BeNull();
            body["status"].Should().BeNull();
            body["correlationId"].Should().BeNull();
        }

        [SetUp]
        public async Task Setup()
        {
            var requestInfo = No.RequestInfo("traceId");
            var middleware = new CoreExceptionLoggingMiddleware(NullLogger.Instance);

            await middleware.Execute(
                requestInfo,
                () => throw new InvalidOperationException("simulated failure")
            );

            _response = (FrontendResponse)requestInfo.FrontendResponse;
        }

        [Test]
        public void It_returns_the_expected_500_body()
        {
            AssertExpectedServerErrorResponse(
                _response,
                "The server encountered an unexpected condition that prevented it from fulfilling the request.",
                "traceId"
            );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Custom_View_Validation_Exception : CoreExceptionLoggingMiddlewareTests
    {
        private FrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var requestInfo = No.RequestInfo("custom-view-trace-id");
            var middleware = new CoreExceptionLoggingMiddleware(NullLogger.Instance);

            await middleware.Execute(
                requestInfo,
                () => throw new CustomViewAuthorizationValidationException(new InvalidOperationException())
            );

            _response = (FrontendResponse)requestInfo.FrontendResponse;
        }

        [Test]
        public void It_returns_a_system_error_response()
        {
            _response.StatusCode.Should().Be(500);
            // A ProblemDetails body must be served as problem+json. The generic unhandled-exception 500
            // above keeps application/json because its body is deliberately not ProblemDetails.
            _response.ContentType.Should().Be("application/problem+json");

            JsonObject body = _response.Body!.AsObject();
            body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:system");
            body["title"]!.GetValue<string>().Should().Be("System Error");
            body["status"]!.GetValue<int>().Should().Be(500);
            body["correlationId"]!.GetValue<string>().Should().Be("custom-view-trace-id");
        }
    }
}
