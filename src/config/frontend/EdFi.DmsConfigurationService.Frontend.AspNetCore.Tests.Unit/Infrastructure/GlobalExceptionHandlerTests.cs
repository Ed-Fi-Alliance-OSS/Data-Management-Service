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

/// <summary>
/// A BadHttpRequestException whose StatusCode is the default 400 (e.g. a route/body binding failure)
/// is handled as a safe, generic Ed-Fi bad-request that never echoes the framework message.
/// </summary>
[TestFixture]
public class Given_a_bad_http_request_exception_with_a_generic_status
{
    private DefaultHttpContext _httpContext = null!;
    private string _responseBody = null!;
    private bool _handled;

    [SetUp]
    public async Task Setup()
    {
        _httpContext = new DefaultHttpContext();
        _httpContext.Response.Body = new MemoryStream();
        // The framework message can embed the offending raw route/body value.
        var exception = new BadHttpRequestException(
            "Failed to read parameter \"long id\" from route value \"not-a-long\"."
        );

        _handled = await new GlobalExceptionHandler().TryHandleAsync(
            _httpContext,
            exception,
            CancellationToken.None
        );

        _httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        _responseBody = await new StreamReader(_httpContext.Response.Body).ReadToEndAsync();
    }

    [Test]
    public void It_handles_the_exception() => _handled.Should().BeTrue();

    [Test]
    public void It_returns_a_generic_bad_request_problem_details()
    {
        _httpContext.Response.StatusCode.Should().Be(400);
        _httpContext.Response.ContentType.Should().Be("application/problem+json");
        _httpContext.Response.Headers["TraceId"].Should().NotBeNullOrEmpty();
        _responseBody.Should().Contain("urn:ed-fi:api:bad-request");
        _responseBody.Should().Contain("The request was invalid.");
    }

    [Test]
    public void It_does_not_leak_the_framework_message()
    {
        _responseBody.Should().NotContain("not-a-long");
        _responseBody.Should().NotContain("Failed to read parameter");
    }
}

/// <summary>
/// A BadHttpRequestException whose StatusCode is 415 (a content-type mismatch raised when
/// ThrowOnBadRequest is enabled, e.g. in Development) is preserved as a structured Ed-Fi 415 rather
/// than being collapsed to 400, and still never echoes the framework message.
/// </summary>
[TestFixture]
public class Given_a_bad_http_request_exception_with_an_unsupported_media_type_status
{
    private DefaultHttpContext _httpContext = null!;
    private string _responseBody = null!;
    private bool _handled;

    [SetUp]
    public async Task Setup()
    {
        _httpContext = new DefaultHttpContext();
        _httpContext.Response.Body = new MemoryStream();
        var exception = new BadHttpRequestException(
            "Unsupported content type \"text/plain\".",
            StatusCodes.Status415UnsupportedMediaType
        );

        _handled = await new GlobalExceptionHandler().TryHandleAsync(
            _httpContext,
            exception,
            CancellationToken.None
        );

        _httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        _responseBody = await new StreamReader(_httpContext.Response.Body).ReadToEndAsync();
    }

    [Test]
    public void It_handles_the_exception() => _handled.Should().BeTrue();

    [Test]
    public void It_returns_a_structured_unsupported_media_type_problem_details()
    {
        _httpContext.Response.StatusCode.Should().Be(415);
        _httpContext.Response.ContentType.Should().Be("application/problem+json");
        _httpContext.Response.Headers["TraceId"].Should().NotBeNullOrEmpty();
        _responseBody.Should().Contain("urn:ed-fi:api:unsupported-media-type");
        _responseBody.Should().Contain("The request content type is not supported.");
    }

    [Test]
    public void It_does_not_leak_the_framework_message()
    {
        _responseBody.Should().NotContain("text/plain");
        _responseBody.Should().NotContain("Unsupported content type");
    }
}
