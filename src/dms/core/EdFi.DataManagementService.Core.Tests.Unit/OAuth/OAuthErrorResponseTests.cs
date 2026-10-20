// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.OAuth;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.OAuth;

[TestFixture]
public class Given_An_OAuth_Error_Response
{
    private const string TraceIdValue = "trace-%/ñ";
    private const string ErrorCode = "invalid_request";
    private const string Description = "The request is invalid.";
    private const string ExpectedChallenge = "Basic realm=\"EdFi.DataManagementService\"";

    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.RequestEntityTooLarge)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.BadGateway)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    public async Task It_writes_the_exact_oauth_json_contract(HttpStatusCode statusCode)
    {
        HttpResponseMessage response = OAuthErrorResponse.Create(
            statusCode,
            ErrorCode,
            Description,
            new TraceId(TraceIdValue)
        );

        response.StatusCode.Should().Be(statusCode);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        body.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo("error", "error_description");
        body.RootElement.GetProperty("error").GetString().Should().Be(ErrorCode);
        body.RootElement.GetProperty("error_description").GetString().Should().Be(Description);
        response
            .Headers.GetValues("X-Correlation-ID")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(Uri.EscapeDataString(TraceIdValue));

        if (statusCode is HttpStatusCode.Unauthorized)
        {
            response
                .Headers.WwwAuthenticate.Select(challenge => challenge.ToString())
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(ExpectedChallenge);
        }
        else
        {
            response.Headers.WwwAuthenticate.Should().BeEmpty();
        }
    }
}
