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

[TestFixture(HttpStatusCode.BadRequest)]
[TestFixture(HttpStatusCode.Unauthorized)]
[TestFixture(HttpStatusCode.RequestEntityTooLarge)]
[TestFixture(HttpStatusCode.TooManyRequests)]
[TestFixture(HttpStatusCode.InternalServerError)]
[TestFixture(HttpStatusCode.BadGateway)]
[TestFixture(HttpStatusCode.ServiceUnavailable)]
public class Given_An_OAuth_Error_Response(HttpStatusCode statusCode)
{
    private const string TraceIdValue = "trace-%/ñ";
    private const string ErrorCode = "invalid_request";
    private const string Description = "The request is invalid.";
    private const string ExpectedChallenge = "Basic realm=\"EdFi.DataManagementService\"";

    private HttpResponseMessage _response = default!;
    private JsonDocument _body = default!;

    [SetUp]
    public async Task SetUp()
    {
        _response = OAuthErrorResponse.Create(statusCode, ErrorCode, Description, new TraceId(TraceIdValue));

        _body = JsonDocument.Parse(await _response.Content.ReadAsStringAsync());
    }

    [TearDown]
    public void TearDown()
    {
        _body.Dispose();
        _response.Dispose();
    }

    [Test]
    public void It_preserves_the_status() => _response.StatusCode.Should().Be(statusCode);

    [Test]
    public void It_uses_the_json_media_type() =>
        _response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

    [Test]
    public void It_writes_an_object() => _body.RootElement.ValueKind.Should().Be(JsonValueKind.Object);

    [Test]
    public void It_writes_exactly_the_oauth_members() =>
        _body
            .RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo("error", "error_description");

    [Test]
    public void It_writes_the_error_code() =>
        _body.RootElement.GetProperty("error").GetString().Should().Be(ErrorCode);

    [Test]
    public void It_writes_the_description() =>
        _body.RootElement.GetProperty("error_description").GetString().Should().Be(Description);

    [Test]
    public void It_encodes_the_correlation_header() =>
        _response
            .Headers.GetValues("X-Correlation-ID")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(Uri.EscapeDataString(TraceIdValue));

    [Test]
    public void It_challenges_only_for_401()
    {
        string[] expectedChallenges = statusCode is HttpStatusCode.Unauthorized ? [ExpectedChallenge] : [];
        _response
            .Headers.WwwAuthenticate.Select(challenge => challenge.ToString())
            .Should()
            .Equal(expectedChallenges);
    }
}
