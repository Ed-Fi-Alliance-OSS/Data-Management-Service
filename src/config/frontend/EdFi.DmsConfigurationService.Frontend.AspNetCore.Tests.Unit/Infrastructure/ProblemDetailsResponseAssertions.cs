// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Shared assertion for the full Ed-Fi Problem Details contract at the HTTP level. Verifies the response
/// status, the application/problem+json media type, every common Problem Details member, and the exact
/// validationErrors / errors shape. Both default to an empty object / empty array; a branch that expects
/// specific contents passes them so the shape is still asserted exactly rather than merely checked for
/// existence. Returns the parsed body so a caller can additionally assert anything branch-specific.
/// </summary>
internal static class ProblemDetailsResponseAssertions
{
    public static async Task<JsonNode> ShouldBeProblemDetailAsync(
        this HttpResponseMessage response,
        HttpStatusCode status,
        string type,
        string title,
        string? detail = null,
        JsonObject? validationErrors = null,
        string[]? errors = null
    )
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        // Exact response shape: the body carries exactly the Ed-Fi contract members, with no framework or
        // ad hoc extras leaking through.
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

        body["type"]!.GetValue<string>().Should().Be(type);
        body["title"]!.GetValue<string>().Should().Be(title);
        body["status"]!.GetValue<int>().Should().Be((int)status);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        // The contract requires validationErrors and errors to always be present, defaulting to an empty
        // object / empty array. Assert the exact shape so a bare call cannot silently accept the wrong
        // contents: {} / [] unless the branch under test supplies specific values.
        body["validationErrors"].Should().NotBeNull();
        JsonObject expectedValidationErrors = validationErrors ?? new JsonObject();
        JsonNode
            .DeepEquals(body["validationErrors"], expectedValidationErrors)
            .Should()
            .BeTrue(
                "validationErrors should be {0} but was {1}",
                expectedValidationErrors.ToJsonString(),
                body["validationErrors"]?.ToJsonString() ?? "null"
            );

        body["errors"].Should().NotBeNull();
        string[] actualErrors = body["errors"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        actualErrors.Should().Equal(errors ?? []);

        if (detail is not null)
        {
            body["detail"]!.GetValue<string>().Should().Be(detail);
        }

        return body;
    }
}
