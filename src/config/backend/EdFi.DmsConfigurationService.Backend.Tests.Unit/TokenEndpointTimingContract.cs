// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DmsConfigurationService.Tests.TimingEvidence;

/// <summary>
/// The response every endpoint timing arm must receive: a 401 whose problem-details <c>errors</c>
/// array holds exactly the <c>invalid_client</c> message. Every failed client authentication answers
/// it, so a run can never accept a build that answers one arm differently.
/// </summary>
internal static class TokenEndpointTimingContract
{
    public const string InvalidClientError = "invalid_client. Invalid client or Invalid client credentials";

    public static bool IsExpected(HttpStatusCode status, string body) =>
        status == HttpStatusCode.Unauthorized && SingleError(body) == InvalidClientError;

    private static string? SingleError(string body)
    {
        try
        {
            return
                JsonNode.Parse(body) is JsonObject root
                && root["errors"] is JsonArray { Count: 1 } errors
                && errors[0] is JsonValue error
                && error.GetValueKind() == JsonValueKind.String
                ? error.GetValue<string>()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
