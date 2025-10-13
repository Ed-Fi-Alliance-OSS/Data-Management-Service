// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
internal class GlobalExceptionHandlerTests
{
    private ILogger<GlobalExceptionHandler> _logger = null!;
    private GlobalExceptionHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _logger = A.Fake<ILogger<GlobalExceptionHandler>>();
        _handler = new GlobalExceptionHandler(_logger);
    }

    [Test]
    public async Task When_handling_BadHttpRequestException_returns_400_with_proper_response()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var exception = new BadHttpRequestException("Invalid request data");

        // Act
        var result = await _handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(400);
        httpContext.Response.ContentType.Should().Be("application/problem+json");
        httpContext.Response.Headers["TraceId"].Should().NotBeNullOrEmpty();

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(httpContext.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        responseBody.Should().Contain("Invalid request data");
    }

    [Test]
    public async Task When_handling_ValidationException_returns_400_with_validation_errors()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var validationFailures = new List<ValidationFailure>
        {
            new("PropertyName", "Validation error message"),
        };
        var exception = new ValidationException(validationFailures);

        // Act
        var result = await _handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(400);
        httpContext.Response.ContentType.Should().Be("application/problem+json");
        httpContext.Response.Headers["TraceId"].Should().NotBeNullOrEmpty();

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(httpContext.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        responseBody.Should().Contain("validationErrors");
    }

    [Test]
    public async Task When_handling_generic_exception_returns_500_with_unknown_error()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var exception = new InvalidOperationException("Unexpected error");

        // Act
        var result = await _handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(500);
        httpContext.Response.ContentType.Should().Be("application/problem+json");
        httpContext.Response.Headers["TraceId"].Should().NotBeNullOrEmpty();

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(httpContext.Response.Body);
        var responseBody = await reader.ReadToEndAsync();
        responseBody.Should().NotBeEmpty();
    }

    [Test]
    public async Task When_handling_exception_logs_error_with_trace_id()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var exception = new Exception("Test exception");

        // Act
        await _handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        // Assert
        A.CallTo(_logger)
            .Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == LogLevel.Error)
            .MustHaveHappened();
    }
}
