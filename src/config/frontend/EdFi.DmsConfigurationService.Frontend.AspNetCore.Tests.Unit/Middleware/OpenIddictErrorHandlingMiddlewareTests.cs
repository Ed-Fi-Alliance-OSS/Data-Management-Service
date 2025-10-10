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
using OpenIddict.Abstractions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Middleware;

[TestFixture]
internal class OpenIddictErrorHandlingMiddlewareTests
{
    private ILogger<OpenIddictErrorHandlingMiddleware> _logger = null!;
    private OpenIddictErrorHandlingMiddleware _middleware = null!;

    [SetUp]
    public void Setup()
    {
        _logger = A.Fake<ILogger<OpenIddictErrorHandlingMiddleware>>();
    }

    [Test]
    public async Task When_no_exception_occurs_calls_next_delegate()
    {
        // Arrange
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        _middleware = new OpenIddictErrorHandlingMiddleware(next, _logger);
        var httpContext = new DefaultHttpContext();

        // Act
        await _middleware.InvokeAsync(httpContext);

        // Assert
        nextCalled.Should().BeTrue();
    }

    [Test]
    public async Task When_exception_on_non_oauth_endpoint_propagates_exception()
    {
        // Arrange
        RequestDelegate next = _ => throw new InvalidOperationException("Test error");
        _middleware = new OpenIddictErrorHandlingMiddleware(next, _logger);
        var httpContext = new DefaultHttpContext { Request = { Path = "/api/vendors" } };

        // Act
        var act = async () => await _middleware.InvokeAsync(httpContext);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    [TestCase("/connect/token")]
    [TestCase("/connect/authorize")]
    [TestCase("/connect/introspect")]
    [TestCase("/connect/revoke")]
    [TestCase("/connect/userinfo")]
    [TestCase("/.well-known/openid_configuration")]
    [TestCase("/.well-known/jwks")]
    public async Task When_exception_on_oauth_endpoint_returns_oauth_error_response(string path)
    {
        // Arrange
        RequestDelegate next = _ => throw new InvalidOperationException("Test error");
        _middleware = new OpenIddictErrorHandlingMiddleware(next, _logger);
        var httpContext = new DefaultHttpContext { Request = { Path = path } };
        httpContext.Response.Body = new MemoryStream();

        // Act
        await _middleware.InvokeAsync(httpContext);

        // Assert
        httpContext.Response.StatusCode.Should().Be(400);
        httpContext.Response.ContentType.Should().Be("application/json");

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(httpContext.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        responseBody.Should().Contain("error");
    }

    [Test]
    public async Task When_ArgumentException_with_client_returns_invalid_client_error()
    {
        // Arrange
        RequestDelegate next = _ => throw new ArgumentException("Invalid client credentials");
        _middleware = new OpenIddictErrorHandlingMiddleware(next, _logger);
        var httpContext = new DefaultHttpContext { Request = { Path = "/connect/token" } };
        httpContext.Response.Body = new MemoryStream();

        // Act
        await _middleware.InvokeAsync(httpContext);

        // Assert
        httpContext.Response.StatusCode.Should().Be(400);
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(httpContext.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        responseBody.Should().Contain(OpenIddictConstants.Errors.InvalidClient);
    }

    [Test]
    public async Task When_ArgumentException_with_grant_returns_unsupported_grant_type_error()
    {
        // Arrange
        RequestDelegate next = _ => throw new ArgumentException("Unsupported grant type");
        _middleware = new OpenIddictErrorHandlingMiddleware(next, _logger);
        var httpContext = new DefaultHttpContext { Request = { Path = "/connect/token" } };
        httpContext.Response.Body = new MemoryStream();

        // Act
        await _middleware.InvokeAsync(httpContext);

        // Assert
        httpContext.Response.StatusCode.Should().Be(400);
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(httpContext.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        responseBody.Should().Contain(OpenIddictConstants.Errors.UnsupportedGrantType);
    }

    [Test]
    public async Task When_ArgumentException_with_scope_returns_invalid_scope_error()
    {
        // Arrange
        RequestDelegate next = _ => throw new ArgumentException("Invalid scope requested");
        _middleware = new OpenIddictErrorHandlingMiddleware(next, _logger);
        var httpContext = new DefaultHttpContext { Request = { Path = "/connect/token" } };
        httpContext.Response.Body = new MemoryStream();

        // Act
        await _middleware.InvokeAsync(httpContext);

        // Assert
        httpContext.Response.StatusCode.Should().Be(400);
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(httpContext.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        responseBody.Should().Contain(OpenIddictConstants.Errors.InvalidScope);
    }

    [Test]
    public async Task When_UnauthorizedAccessException_returns_access_denied_error()
    {
        // Arrange
        RequestDelegate next = _ => throw new UnauthorizedAccessException("Access denied");
        _middleware = new OpenIddictErrorHandlingMiddleware(next, _logger);
        var httpContext = new DefaultHttpContext { Request = { Path = "/connect/token" } };
        httpContext.Response.Body = new MemoryStream();

        // Act
        await _middleware.InvokeAsync(httpContext);

        // Assert
        httpContext.Response.StatusCode.Should().Be(400);
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(httpContext.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        responseBody.Should().Contain(OpenIddictConstants.Errors.AccessDenied);
    }

    [Test]
    public async Task When_InvalidOperationException_with_token_returns_invalid_token_error()
    {
        // Arrange
        RequestDelegate next = _ => throw new InvalidOperationException("Invalid token provided");
        _middleware = new OpenIddictErrorHandlingMiddleware(next, _logger);
        var httpContext = new DefaultHttpContext { Request = { Path = "/connect/token" } };
        httpContext.Response.Body = new MemoryStream();

        // Act
        await _middleware.InvokeAsync(httpContext);

        // Assert
        httpContext.Response.StatusCode.Should().Be(400);
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(httpContext.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        responseBody.Should().Contain(OpenIddictConstants.Errors.InvalidToken);
    }

    [Test]
    public async Task When_unknown_exception_type_returns_server_error()
    {
        // Arrange
        RequestDelegate next = _ => throw new NotImplementedException("Not implemented");
        _middleware = new OpenIddictErrorHandlingMiddleware(next, _logger);
        var httpContext = new DefaultHttpContext { Request = { Path = "/connect/token" } };
        httpContext.Response.Body = new MemoryStream();

        // Act
        await _middleware.InvokeAsync(httpContext);

        // Assert
        httpContext.Response.StatusCode.Should().Be(400);
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(httpContext.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        responseBody.Should().Contain(OpenIddictConstants.Errors.ServerError);
    }

    [Test]
    public async Task When_exception_occurs_logs_error()
    {
        // Arrange
        RequestDelegate next = _ => throw new InvalidOperationException("Test error");
        _middleware = new OpenIddictErrorHandlingMiddleware(next, _logger);
        var httpContext = new DefaultHttpContext { Request = { Path = "/connect/token" } };
        httpContext.Response.Body = new MemoryStream();

        // Act
        await _middleware.InvokeAsync(httpContext);

        // Assert
        A.CallTo(_logger)
            .Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Error)
            .MustHaveHappened();
    }
}
