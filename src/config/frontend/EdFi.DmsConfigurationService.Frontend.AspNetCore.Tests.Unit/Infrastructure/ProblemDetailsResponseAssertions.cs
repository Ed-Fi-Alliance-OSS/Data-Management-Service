// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Shared assertions for the Ed-Fi Problem Details contract at the HTTP level. Verifies the response
/// status, the application/problem+json media type, and the common Problem Details members, and returns
/// the parsed body so a caller can additionally assert branch-specific validationErrors/errors without
/// hiding which branch is under test.
/// </summary>
internal static class ProblemDetailsResponseAssertions
{
    public static async Task<JsonNode> ShouldBeProblemDetailAsync(
        this HttpResponseMessage response,
        HttpStatusCode status,
        string type,
        string title,
        string? detail = null
    )
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["type"]!.GetValue<string>().Should().Be(type);
        body["title"]!.GetValue<string>().Should().Be(title);
        body["status"]!.GetValue<int>().Should().Be((int)status);
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        body["validationErrors"].Should().NotBeNull();
        body["errors"].Should().NotBeNull();

        if (detail is not null)
        {
            body["detail"]!.GetValue<string>().Should().Be(detail);
        }

        return body;
    }
}
