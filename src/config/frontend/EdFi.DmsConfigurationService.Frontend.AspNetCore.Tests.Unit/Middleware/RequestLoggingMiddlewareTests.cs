// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using FakeItEasy;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
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
        public async Task When_middleware_receives_validation_errors()
        {
            // Arrange
            string validationError = "Validation exception occurred.";
            var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

            var exception = new ValidationException(
                [new ValidationFailure { PropertyName = "test", ErrorMessage = validationError }]
            );

            A.CallTo(() => _next.Invoke(httpContext)).Throws(exception);

            var middleWare = new RequestLoggingMiddleware(_next);

            // Act
            await middleWare.Invoke(httpContext, _logger);

            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
            string responseBody = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
            int statusCode = httpContext.Response.StatusCode;

            // Assert
            responseBody.Should().Contain(validationError);
            statusCode.Should().Be((int)HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task When_middleware_receives_server_errors()
        {
            // Arrange
            string error =
                "The server encountered an unexpected condition that prevented it from fulfilling the request.";
            var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

            var exception = new Exception(error);
            A.CallTo(() => _next.Invoke(httpContext)).Throws(exception);

            var middleWare = new RequestLoggingMiddleware(_next);

            // Act
            await middleWare.Invoke(httpContext, _logger);

            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
            string responseBody = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
            int statusCode = httpContext.Response.StatusCode;

            // Assert
            responseBody.Should().Contain(error);
            statusCode.Should().Be((int)HttpStatusCode.InternalServerError);
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

        [Test]
        public async Task When_middleware_receives_keycloack_exception_with_404_status_code()
        {
            // Arrange
            var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
            var exception = new KeycloakException(new KeycloakError.NotFound("Status Code 404"));

            A.CallTo(() => _next.Invoke(httpContext)).Throws(exception);

            var middleWare = new RequestLoggingMiddleware(_next);

            // Act
            await middleWare.Invoke(httpContext, _logger);

            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
            string responseBody = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
            int statusCode = httpContext.Response.StatusCode;

            // Assert
            responseBody.Should().Contain("Invalid realm.");
            statusCode.Should().Be((int)HttpStatusCode.NotFound);
        }

        [Test]
        public async Task When_middleware_receives_keycloack_exception_bad_gateway()
        {
            // Arrange
            var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
            var exception = new KeycloakException(
                new KeycloakError.Unreachable("No connection could be made")
            );

            A.CallTo(() => _next.Invoke(httpContext)).Throws(exception);

            var middleWare = new RequestLoggingMiddleware(_next);

            // Act
            await middleWare.Invoke(httpContext, _logger);

            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
            string responseBody = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
            int statusCode = httpContext.Response.StatusCode;

            // Assert
            responseBody.Should().Contain("Keycloak is unreachable.");
            statusCode.Should().Be((int)HttpStatusCode.BadGateway);
        }

        [Test]
        public async Task When_middleware_receives_bad_http_request_exception()
        {
            // Arrange
            var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
            var exception = new BadHttpRequestException("Bad request format.");

            A.CallTo(() => _next.Invoke(httpContext)).Throws(exception);

            var middleWare = new RequestLoggingMiddleware(_next);

            // Act
            await middleWare.Invoke(httpContext, _logger);

            httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
            string responseBody = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
            int statusCode = httpContext.Response.StatusCode;

            // Assert
            responseBody.Should().Contain("Bad request format.");
            statusCode.Should().Be((int)HttpStatusCode.BadRequest);
        }
    }
}
