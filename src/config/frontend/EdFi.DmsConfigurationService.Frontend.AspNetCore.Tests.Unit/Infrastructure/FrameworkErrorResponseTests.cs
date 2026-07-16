// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Covers the non-throwing status-code-pages mapping used by UseStatusCodePages. A real oversized-body
/// 413 cannot be produced under the in-memory test server (it does not enforce request body size limits),
/// so the 413 branch — and every other empty-framework-status branch — is exercised here by executing the
/// mapped result and asserting the response the client would receive.
/// </summary>
[TestFixture]
internal class FrameworkErrorResponseTests
{
    private static readonly object[] MappedStatusCases =
    [
        new object[] { 400, "urn:ed-fi:api:bad-request", "Bad Request", "The request was invalid." },
        new object[] { 404, "urn:ed-fi:api:not-found", "Not Found", "The requested resource was not found." },
        new object[]
        {
            405,
            "urn:ed-fi:api:method-not-allowed",
            "Method Not Allowed",
            "The request method is not allowed for this resource.",
        },
        new object[]
        {
            415,
            "urn:ed-fi:api:unsupported-media-type",
            "Unsupported Media Type",
            "The request content type is not supported.",
        },
        new object[] { 413, "urn:ed-fi:api:bad-request", "Bad Request", "The request payload is too large." },
    ];

    [TestCaseSource(nameof(MappedStatusCases))]
    public async Task It_maps_an_empty_framework_status_to_the_full_ed_fi_contract(
        int statusCode,
        string type,
        string title,
        string detail
    )
    {
        IResult? result = FrameworkErrorResponse.ForEmptyStatusCode(statusCode, "trace-123");
        result.Should().NotBeNull();

        (int bodyStatus, JsonNode body) = await ExecuteAsync(result!);

        bodyStatus.Should().Be(statusCode);
        body["type"]!.GetValue<string>().Should().Be(type);
        body["title"]!.GetValue<string>().Should().Be(title);
        body["status"]!.GetValue<int>().Should().Be(statusCode);
        body["detail"]!.GetValue<string>().Should().Be(detail);
        body["correlationId"]!.GetValue<string>().Should().Be("trace-123");
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
    }

    [TestCase(403)]
    [TestCase(418)]
    [TestCase(500)]
    public void It_returns_null_for_a_status_it_does_not_map(int statusCode) =>
        FrameworkErrorResponse.ForEmptyStatusCode(statusCode, "trace-123").Should().BeNull();

    // Executes the result against a bare HTTP context and returns the written status and parsed body,
    // mirroring what the client would receive from the status-code-pages handler.
    private static async Task<(int Status, JsonNode Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        string raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, JsonNode.Parse(raw)!);
    }
}
