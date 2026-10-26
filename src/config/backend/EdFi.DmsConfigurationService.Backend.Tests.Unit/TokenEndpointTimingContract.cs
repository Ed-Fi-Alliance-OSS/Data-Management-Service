// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DmsConfigurationService.Tests.TimingEvidence;

/// <summary>The <c>/connect/token</c> error contract of the build an endpoint timing run measures.</summary>
internal enum TokenEndpointContract
{
    /// <summary>Every failed client authentication answers <c>invalid_client</c>.</summary>
    Current,

    /// <summary>The build before the fix, measured once for contrast: a known client's wrong secret answered <c>unauthorized_client</c>.</summary>
    PreFix,
}

/// <summary>
/// The response each endpoint timing arm must receive. Only the wrong-secret arm under the pre-fix
/// contract expects <c>unauthorized_client</c>. Every other arm, and every arm under the current
/// contract, expects <c>invalid_client</c>, so a current-contract run can never accept the old code.
/// A response is expected only when it is a 401 whose problem-details <c>errors</c> array holds
/// exactly that one message.
/// </summary>
internal static class TokenEndpointTimingContract
{
    public const string WrongSecretArm = "wrong_secret";
    public const string InvalidClientError = "invalid_client. Invalid client or Invalid client credentials";
    public const string PreFixWrongSecretError =
        "unauthorized_client. Invalid client or Invalid client credentials";

    public static TokenEndpointContract Parse(string? value) =>
        value switch
        {
            null or "" or "current" => TokenEndpointContract.Current,
            "pre-fix" => TokenEndpointContract.PreFix,
            _ => throw new InvalidOperationException(
                $"TIMING_EVIDENCE_CONTRACT must be 'current' or 'pre-fix', not '{value}'."
            ),
        };

    public static string Name(TokenEndpointContract contract) =>
        contract == TokenEndpointContract.PreFix ? "pre-fix" : "current";

    public static string ExpectedError(TokenEndpointContract contract, string arm) =>
        contract == TokenEndpointContract.PreFix && arm == WrongSecretArm
            ? PreFixWrongSecretError
            : InvalidClientError;

    public static bool IsExpected(
        TokenEndpointContract contract,
        string arm,
        HttpStatusCode status,
        string body
    ) => status == HttpStatusCode.Unauthorized && SingleError(body) == ExpectedError(contract, arm);

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
