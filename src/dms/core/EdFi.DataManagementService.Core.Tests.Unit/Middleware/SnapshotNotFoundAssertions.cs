// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using FluentAssertions;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// The exact Snapshot Not Found response, asserted field by field.
/// </summary>
/// <remarks>
/// Several production sites answer with this response - the selection step for a read with no
/// snapshot configured, and each connection-unavailable translation site for a snapshot that could not
/// be reached - and a client must not be able to tell them apart. Every one of them is therefore
/// checked against this single definition rather than against a status code, so a body that drifted at
/// one site could not pass.
/// </remarks>
internal static class SnapshotNotFoundAssertions
{
    public static void ShouldBeSnapshotNotFound(this IFrontendResponse response, string expectedCorrelationId)
    {
        response.StatusCode.Should().Be(404);
        response.ContentType.Should().Be("application/problem+json");
        response.Headers.Should().BeEmpty();

        JsonObject body = response.Body!.AsObject();

        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
        body["title"]!.GetValue<string>().Should().Be("Not Found");
        body["status"]!.GetValue<int>().Should().Be(404);
        body["detail"]!.GetValue<string>().Should().Be("Snapshot not found.");
        body["correlationId"]!.GetValue<string>().Should().Be(expectedCorrelationId);
        body["validationErrors"]!.AsObject().Count.Should().Be(0);
        body["errors"]!.AsArray().Count.Should().Be(0);
    }
}
