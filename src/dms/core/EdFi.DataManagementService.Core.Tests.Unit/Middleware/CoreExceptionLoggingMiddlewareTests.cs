// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class CoreExceptionLoggingMiddlewareTests
{
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
}
