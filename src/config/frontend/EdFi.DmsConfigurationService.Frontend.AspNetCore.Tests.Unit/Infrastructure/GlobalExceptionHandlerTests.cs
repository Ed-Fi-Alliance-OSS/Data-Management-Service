// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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

        var body = JsonNode.Parse(responseBody)!;
        body.AsObject()
            .Select(member => member.Key)
            .Should()
            .BeEquivalentTo(
                "detail",
                "type",
                "title",
                "status",
                "correlationId",
                "validationErrors",
                "errors"
            );
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:data");
        body["title"]!.GetValue<string>().Should().Be("Data Validation Failed");
        body["detail"]!
            .GetValue<string>()
            .Should()
            .Be("Data validation failed. See 'validationErrors' for details.");
        body["status"]!.GetValue<int>().Should().Be(400);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["errors"]!.AsArray().Count.Should().Be(0);

        var validationErrors = body["validationErrors"]!.AsObject();
        validationErrors.Count.Should().Be(1);
        validationErrors["PropertyName"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .Should()
            .Equal("Validation error message");
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

        var body = JsonNode.Parse(responseBody)!;
        body.AsObject()
            .Select(member => member.Key)
            .Should()
            .BeEquivalentTo(
                "detail",
                "type",
                "title",
                "status",
                "correlationId",
                "validationErrors",
                "errors"
            );
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:internal-server-error");
        body["title"]!.GetValue<string>().Should().Be("Internal Server Error");
        body["detail"]!.GetValue<string>().Should().BeEmpty();
        body["status"]!.GetValue<int>().Should().Be(500);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);

        // The exception message must never be surfaced in the response body.
        responseBody.Should().NotContain("Unexpected error");
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

        var body = JsonNode.Parse(_responseBody)!;
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
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

        var body = JsonNode.Parse(_responseBody)!;
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
    }

    [Test]
    public void It_does_not_leak_the_framework_message()
    {
        _responseBody.Should().NotContain("text/plain");
        _responseBody.Should().NotContain("Unsupported content type");
    }
}

/// <summary>
/// A BadHttpRequestException whose StatusCode is 413 (payload too large) preserves its status code and
/// returns a machine-readable Ed-Fi Problem Details body — never an empty response. The Ed-Fi Error
/// Response Knowledge Base defines no dedicated 413 type, so the generic bad-request type is used with
/// the 413 status preserved; the framework message is never echoed.
/// </summary>
[TestFixture]
public class Given_a_bad_http_request_exception_with_a_payload_too_large_status
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
            "Request body too large. The max request body size is 30000000 bytes.",
            StatusCodes.Status413PayloadTooLarge
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
    public void It_returns_the_generic_bad_request_contract_with_the_413_status()
    {
        _httpContext.Response.StatusCode.Should().Be(413);
        _httpContext.Response.ContentType.Should().Be("application/problem+json");
        _httpContext.Response.Headers["TraceId"].Should().NotBeNullOrEmpty();

        var body = JsonNode.Parse(_responseBody)!;
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
        body["title"]!.GetValue<string>().Should().Be("Bad Request");
        body["status"]!.GetValue<int>().Should().Be(413);
        body["detail"]!.GetValue<string>().Should().Be("The request payload is too large.");
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
    }

    [Test]
    public void It_does_not_invent_a_nonstandard_type()
    {
        // The Ed-Fi Error Response Knowledge Base defines no payload-too-large type.
        _responseBody.Should().NotContain("payload-too-large");
    }

    [Test]
    public void It_does_not_leak_the_framework_message()
    {
        _responseBody.Should().NotContain("30000000");
        _responseBody.Should().NotContain("Request body too large");
    }
}

/// <summary>
/// A BadHttpRequestException whose StatusCode is neither 400, 413, nor 415 (any other framework request
/// status) is preserved rather than collapsed to 400: the generic bad-request contract is returned with
/// that exact status in both the HTTP status and the body, since the Ed-Fi Error Response Knowledge Base
/// defines no more specific type; the framework message is never echoed.
/// </summary>
[TestFixture]
public class Given_a_bad_http_request_exception_with_an_other_framework_status
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
            "Request header fields too large.",
            StatusCodes.Status431RequestHeaderFieldsTooLarge
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
    public void It_preserves_the_status_in_the_generic_bad_request_contract()
    {
        _httpContext.Response.StatusCode.Should().Be(431);
        _httpContext.Response.ContentType.Should().Be("application/problem+json");
        _httpContext.Response.Headers["TraceId"].Should().NotBeNullOrEmpty();

        var body = JsonNode.Parse(_responseBody)!;
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
        body["title"]!.GetValue<string>().Should().Be("Bad Request");
        body["status"]!.GetValue<int>().Should().Be(431);
        body["detail"]!.GetValue<string>().Should().Be("The request was invalid.");
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
    }

    [Test]
    public void It_does_not_leak_the_framework_message()
    {
        _responseBody.Should().NotContain("Request header fields too large");
    }
}
