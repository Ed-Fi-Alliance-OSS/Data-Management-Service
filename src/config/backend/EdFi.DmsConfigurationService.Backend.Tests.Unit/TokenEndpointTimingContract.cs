// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DmsConfigurationService.Tests.TimingEvidence;

/// <summary>
/// The response every endpoint timing arm must receive. The arms send form credentials, so it is the
/// <c>400</c> OAuth <c>invalid_client</c> error (RFC 6749 §5.2) with the fixed description and nothing
/// else. Every failed client authentication answers it, so a run can never accept a build that
/// answers one arm differently.
/// </summary>
internal static class TokenEndpointTimingContract
{
    public const string InvalidClientError = "invalid_client";
    public const string InvalidClientDescription = "Invalid client or Invalid client credentials";

    public static bool IsExpected(HttpStatusCode status, string body) =>
        status == HttpStatusCode.BadRequest && IsInvalidClientBody(body);

    private static bool IsInvalidClientBody(string body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject { Count: 2 } root
                && StringMember(root, "error") == InvalidClientError
                && StringMember(root, "error_description") == InvalidClientDescription;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? StringMember(JsonObject root, string name) =>
        root[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;
}
