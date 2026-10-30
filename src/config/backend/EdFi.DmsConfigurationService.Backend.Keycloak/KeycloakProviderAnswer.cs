// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Unicode;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

/// <summary>
/// Handling of Keycloak's answers shared by the delegated revocation and introspection requests:
/// the client id length Keycloak can hold, the parsing of an answer's JSON members, the loggable
/// provider error categories and the failure classification and logging helpers. Nothing here logs
/// anything from a provider body or a dependency exception; what each request reads from the parsed
/// members, and how it reports them, stays with that request.
/// </summary>
internal static class KeycloakProviderAnswer
{
    /// <summary>
    /// The longest client id Keycloak can hold, in code points: Keycloak keeps it in a 255-character
    /// column, and every database counts a code point as at least one character (PostgreSQL counts code
    /// points, H2 UTF-16 code units).
    /// </summary>
    internal const int MaxClientIdCodePoints = 255;

    /// <summary>The members read from an RFC 6749 §5.2 error answer.</summary>
    internal static readonly HashSet<string> ErrorMembers = new(StringComparer.Ordinal)
    {
        "error",
        "error_description",
    };

    /// <summary>The provider <c>error</c> values that may be logged verbatim; every other value is <c>unrecognized</c>.</summary>
    private static readonly HashSet<string> _loggableProviderErrors = new(StringComparer.Ordinal)
    {
        "invalid_request",
        "invalid_client",
        "unauthorized_client",
        "unsupported_token_type",
        "invalid_token",
        "server_error",
        "temporarily_unavailable",
    };

    /// <summary>True when the client id has more code points than any Keycloak client id can hold.</summary>
    internal static bool IsLongerThanAnyClientId(string clientId) =>
        clientId.EnumerateRunes().Skip(MaxClientIdCodePoints).Any();

    /// <summary>The provider <c>error</c> when it is allowlisted for logging, otherwise <c>unrecognized</c>.</summary>
    internal static string ProviderErrorCategory(string? error) =>
        error is not null && _loggableProviderErrors.Contains(error) ? error : "unrecognized";

    /// <summary>Connection, name resolution, TLS or proxy failures: the request never reached Keycloak.</summary>
    internal static bool IsUndelivered(HttpRequestError error) =>
        error
            is HttpRequestError.ConnectionError
                or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError;

    /// <summary>The type names of an exception and its inner exceptions, outermost first.</summary>
    internal static string ExceptionTypeChain(Exception exception)
    {
        string name = exception.GetType().FullName ?? exception.GetType().Name;
        return exception.InnerException is { } inner ? $"{name} -> {ExceptionTypeChain(inner)}" : name;
    }

    /// <summary>
    /// The <paramref name="read"/> members of a JSON object, each cloned so it outlives the parsed
    /// document. Null (unparseable) when the body is missing, empty, not UTF-8, not JSON or not an
    /// object, when any member name cannot be decoded, when a read member is repeated, or when a read
    /// member is a string that cannot be decoded: a repeated or undecodable member would make the
    /// caller's exact comparisons or reported values ambiguous. A member that is not read may repeat.
    /// With <paramref name="decodeArrayStrings"/>, the strings directly inside a read array member are
    /// decoded too; without it an array member is kept undecoded, so the caller must not read strings
    /// from it. Every name is decoded through <see cref="JsonProperty.Name"/> rather than compared with
    /// <c>NameEquals</c>, which can answer "not equal" without decoding, so an escaped name is
    /// recognized, the outcome never depends on which comparison happened to run, and an escaped,
    /// unpaired surrogate such as <c>"\uD800"</c>, valid UTF-8 and valid JSON syntax, is rejected
    /// wherever it is: such a name could be any read member, even beside well-formed members.
    /// </summary>
    internal static Dictionary<string, JsonElement>? ParseMembers(
        byte[]? body,
        IReadOnlySet<string> read,
        bool decodeArrayStrings
    )
    {
        // Checked explicitly: JsonDocument accepts invalid UTF-8 inside a string and only fails
        // when the value is read, which would surface as a fault rather than an unparseable body.
        if (body is null || body.Length == 0 || !Utf8.IsValid(body))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            Dictionary<string, JsonElement> members = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!TryDecodeName(property, out string name))
                {
                    return null;
                }

                if (!read.Contains(name))
                {
                    continue;
                }

                if (
                    !StringsDecode(property.Value, decodeArrayStrings)
                    || !members.TryAdd(name, property.Value.Clone())
                )
                {
                    return null;
                }
            }

            return members;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The member as a string; null when it is absent or not a string. Safe on members returned by
    /// <see cref="ParseMembers"/>, which decoded every string member.
    /// </summary>
    internal static string? StringMember(Dictionary<string, JsonElement> members, string name) =>
        members.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// False when the name cannot be decoded: <see cref="JsonProperty.Name"/> rejects an escaped,
    /// unpaired surrogate with <see cref="InvalidOperationException"/>. Only the decoding is guarded.
    /// </summary>
    private static bool TryDecodeName(JsonProperty property, out string name)
    {
        try
        {
            name = property.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            name = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// False when the value is a string, or with <paramref name="inArrays"/> an array holding a string,
    /// that cannot be decoded, so the caller can read every string it accepts without a fault. Only
    /// <c>GetString</c> is guarded, and only for a string element, so any other fault still escapes.
    /// </summary>
    private static bool StringsDecode(JsonElement value, bool inArrays)
    {
        IEnumerable<JsonElement> strings = value.ValueKind switch
        {
            JsonValueKind.String => [value],
            JsonValueKind.Array when inArrays => value
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String),
            _ => [],
        };

        foreach (JsonElement text in strings)
        {
            try
            {
                _ = text.GetString();
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return true;
    }
}
