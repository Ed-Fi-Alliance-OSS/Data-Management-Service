// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using FakeItEasy;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

internal class RequestLoggingMiddlewareTests
{
    [TestFixture]
    public class MiddlewareTests
    {
        private RequestDelegate _next;
        private ILogger<RequestLoggingMiddleware> _logger;

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
        public async Task When_middleware_receives_validation_errors()
        {
            // Arrange
            var validationError = "Validation exception occurred.";
            var httpContext = new DefaultHttpContext();
            httpContext.Response.Body = new MemoryStream();

            var exception = new ValidationException([new ValidationFailure { PropertyName = "test", ErrorMessage = validationError }]);

            A.CallTo(() => _next.Invoke(httpContext)).Throws(exception);

            var middleWare = new RequestLoggingMiddleware(_next);

            // Act
            await middleWare.Invoke(httpContext, _logger);

            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
            var statusCode = httpContext.Response.StatusCode;

            // Assert
            responseBody.Should().Contain(validationError);
            statusCode.Should().Be((int)HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task When_middleware_receives_server_errors()
        {
            // Arrange
            var error = "The server encountered an unexpected condition that prevented it from fulfilling the request.";
            var httpContext = new DefaultHttpContext();
            httpContext.Response.Body = new MemoryStream();

            var exception = new Exception(error);
            A.CallTo(() => _next.Invoke(httpContext)).Throws(exception);

            var middleWare = new RequestLoggingMiddleware(_next);

            // Act
            await middleWare.Invoke(httpContext, _logger);

            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
            var statusCode = httpContext.Response.StatusCode;

            // Assert
            responseBody.Should().Contain(error);
            statusCode.Should().Be((int)HttpStatusCode.InternalServerError);
        }
    }
}
