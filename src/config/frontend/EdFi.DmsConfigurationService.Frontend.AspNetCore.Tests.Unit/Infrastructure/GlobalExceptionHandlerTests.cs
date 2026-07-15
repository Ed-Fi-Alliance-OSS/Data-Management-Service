// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

[TestFixture]
internal class GlobalExceptionHandlerTests
{
    private GlobalExceptionHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new GlobalExceptionHandler();
    }

    [Test]
    public async Task When_handling_BadHttpRequestException_returns_a_safe_generic_400_without_leaking_the_message()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        // A BadHttpRequestException message can embed the offending raw route/body value.
        var exception = new BadHttpRequestException(
            "Failed to read parameter \"long id\" from route value \"not-a-long\"."
        );

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

        // The framework message (and the raw value it embeds) must not be echoed to the caller.
        responseBody.Should().NotContain("not-a-long");
        responseBody.Should().NotContain("Failed to read parameter");
        responseBody.Should().Contain("The request was invalid.");
        responseBody.Should().Contain("urn:ed-fi:api:bad-request");
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
}
