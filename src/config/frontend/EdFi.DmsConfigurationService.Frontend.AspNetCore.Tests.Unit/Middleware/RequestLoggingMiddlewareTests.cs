// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

[TestFixture]
internal class RequestLoggingMiddlewareTests
{
    [TestFixture]
    public class MiddlewareTests
    {
        private RequestDelegate _next = null!;
        private ILogger<RequestLoggingMiddleware> _logger = null!;

        [SetUp]
        public void Setup()
        {
            _next = A.Fake<RequestDelegate>();
            _logger = A.Fake<ILogger<RequestLoggingMiddleware>>();
        }

        [Test]
        public async Task When_middleware_logs_information_and_calls_next()
        {
            // Arrange
            var middleWare = new RequestLoggingMiddleware(_next);
            var httpContext = A.Fake<HttpContext>();

            A.CallTo(() => httpContext.Request.Path).Returns("/test-path");

            // Act
            await middleWare.Invoke(httpContext, _logger);

            // Assert
            A.CallTo(() => _next(httpContext)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task When_request_path_is_well_known_sets_trace_id_and_request_path()
        {
            // Arrange
            var middleWare = new RequestLoggingMiddleware(_next);
            var httpContext = new DefaultHttpContext
            {
                Request = { Path = "/.well-known/openid-configuration" },
            };

            // Act
            await middleWare.Invoke(httpContext, _logger);

            // Assert
            httpContext.TraceIdentifier.Should().NotBeNullOrEmpty();
            httpContext.Request.Path.Value.Should().Be("/.well-known/openid-configuration");
        }
    }
}
