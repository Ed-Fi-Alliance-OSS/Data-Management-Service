// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Unicode;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

/// <summary>
/// RFC 7662 introspection for the Keycloak provider, delegated to the realm's own introspection
/// endpoint with the caller's credentials. Keycloak authenticates the caller, and refuses a public
/// client itself (I-10), but it checks no ownership (I-03, I-04: any confidential client is told
/// about any token), so CMS decides ownership from the answer: only an active access token whose
/// <c>azp</c>, and <c>client_id</c> when present, equal the caller's id ordinally is reported, and only
/// its RFC 7662 members. Keycloak's answer is mapped only through the responses characterized on
/// 26.1.4 and 26.7.5 (<c>KeycloakIntrospectionCharacterizationTests</c>, rows I-00 to I-21); anything
/// else, a timeout or an unreachable host included, is
/// <see cref="TokenIntrospectionResult.TemporarilyUnavailable"/>: one call both authenticates and
/// decides, so without a usable answer an authenticated caller cannot be told apart from an
/// unauthenticated one. Nothing from the caller, a provider body or a dependency exception reaches a log
/// except the sanitized client id, the provider status code, an allowlisted provider error category,
/// fixed member names and exception type names, as for revocation (DMS-1327 D-15).
/// </summary>
public sealed class KeycloakTokenIntrospectionManager(
    KeycloakContext keycloakContext,
    IHttpClientFactory httpClientFactory,
    ILogger<KeycloakTokenIntrospectionManager> logger
) : ITokenIntrospectionManager
{
    /// <summary>
    /// Keycloak's <c>error_description</c> for a public client, copied from the characterization (I-10,
    /// identical on 26.1.4 and 26.7.5). It is the only 403 answered as <c>invalid_client</c>, and only
    /// together with <c>invalid_request</c>.
    /// </summary>
    internal const string PublicClientRefusalDescription = "Client not allowed.";

    /// <summary>The largest answer read; a larger one is treated as unparseable.</summary>
    internal const int MaxResponseBodyBytes = KeycloakProviderAnswer.MaxResponseBodyBytes;

    /// <summary>
    /// The named client for the delegated introspection request, registered with automatic redirects
    /// off: a followed redirect could turn an unusable answer into a token outcome, and on a 307 or 308
    /// would resend the form, the caller's secret and token included, to the redirect target. A 3xx is
    /// then answered here like any other unrecognized status.
    /// </summary>
    public const string HttpClientName = "KeycloakIntrospectionClient";

    /// <summary>The <c>typ</c> of a Keycloak access token; a refresh token is <c>Refresh</c> (I-16).</summary>
    private const string AccessTokenType = "Bearer";

    private const string TimeoutReason = "timeout";
    private const string UnreachableReason = "unreachable";
    private const string ResponseLostReason = "response-lost";
    private const string UnparseableReason = "unparseable";
    private const string UnexpectedReason = "unexpected";
    private const string ProviderStatusReason = "provider-status";
    private const string UnrecognizedResponseReason = "unrecognized-response";

    /// <summary>The members read from a 200 answer. Every other member, Keycloak's own claims included, is ignored.</summary>
    private static readonly HashSet<string> _answerMembers = new(StringComparer.Ordinal)
    {
        "active",
        "azp",
        "client_id",
        "typ",
        "scope",
        "exp",
        "iat",
        "sub",
        "aud",
        "iss",
    };

    /// <summary>The members read from an RFC 6749 §5.2 error answer.</summary>
    private static readonly HashSet<string> _errorMembers = new(StringComparer.Ordinal)
    {
        "error",
        "error_description",
    };

    public async Task<TokenIntrospectionResult> IntrospectTokenAsync(
        TokenIntrospectionRequest request,
        CancellationToken cancellationToken
    )
    {
        // The endpoint already refuses a request without both credentials; repeated here so that no
        // caller of this manager can reach the provider with a secretless request.
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            return new TokenIntrospectionResult.InvalidClient();
        }

        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);

        // Credentials always travel as form fields (client_secret_post), so Keycloak sees exactly one
        // mechanism. token_type_hint is never forwarded: RFC 7662 §2.1 requires the search to extend
        // past a wrong hint, and 26.7.5 answers an access token sent with refresh_token as inactive
        // (I-17), which would hide the caller's own live token.
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{keycloakContext.Url}/realms/{keycloakContext.Realm}/protocol/openid-connect/token/introspect"
        );
        message.Content = new FormUrlEncodedContent([
            new("client_id", request.ClientId),
            new("client_secret", request.ClientSecret),
            new("token", request.Token),
        ]);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            // The caller is still waiting, so this is the client's timeout.
            return IntrospectionFailed(TimeoutReason, request.ClientId, ex);
        }
        catch (HttpRequestException ex)
        {
            return IntrospectionFailed(
                KeycloakProviderAnswer.IsUndelivered(ex.HttpRequestError)
                    ? UnreachableReason
                    : ResponseLostReason,
                request.ClientId,
                ex
            );
        }
        catch (Exception ex)
        {
            return IntrospectionFailed(UnexpectedReason, request.ClientId, ex);
        }

        using (response)
        {
            // Headers were awaited under the client's own timeout; the body is read under a fresh
            // budget of the same length, because ResponseHeadersRead leaves it outside that timeout.
            byte[]? body;
            try
            {
                using var bodyBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bodyBudget.CancelAfter(httpClient.Timeout);
                body = await KeycloakProviderAnswer.ReadBoundedAsync(response.Content, bodyBudget.Token);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex)
                when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                return IntrospectionFailed(ResponseLostReason, request.ClientId, ex);
            }
            catch (Exception ex)
            {
                return IntrospectionFailed(UnexpectedReason, request.ClientId, ex);
            }

            return response.StatusCode == HttpStatusCode.OK
                ? MapAnswer(ParseMembers(body, _answerMembers), request.ClientId)
                : MapErrorResponse(
                    (int)response.StatusCode,
                    ParseMembers(body, _errorMembers),
                    request.ClientId
                );
        }
    }

    /// <summary>
    /// A 200 answer (I-01 to I-05, I-13 to I-17, I-19). <c>active:false</c> is every unknown, invalid,
    /// expired or revoked token. An active answer is a token outcome only for the caller's own access
    /// token; ownership is decided before the token type, so another client's token is inactive whatever
    /// its type. Members are required to have exactly the kinds Keycloak was observed to send.
    /// </summary>
    private TokenIntrospectionResult MapAnswer(Dictionary<string, JsonElement>? members, string clientId)
    {
        if (members is null)
        {
            return AnswerNotUsable(
                UnparseableReason,
                clientId,
                "the body is not a JSON object with unique, decodable members"
            );
        }

        if (
            !members.TryGetValue("active", out JsonElement active)
            || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        )
        {
            return AnswerNotUsable(
                UnrecognizedResponseReason,
                clientId,
                "active is missing or not a boolean"
            );
        }

        if (active.ValueKind == JsonValueKind.False)
        {
            logger.LogDebug(
                "Keycloak answered the introspection request for client {ClientId}: the token is not active",
                LoggingUtility.SanitizeForLog(clientId)
            );
            return new TokenIntrospectionResult.Inactive();
        }

        if (RequiredString(members, "azp") is not { } azp)
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "azp is missing or not a string");
        }

        if (!TryOptionalString(members, "client_id", out string? tokenClientId))
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "client_id is not a string");
        }

        // Ordinal: Keycloak client ids are case-sensitive, so a case variant is another client.
        if (
            !string.Equals(azp, clientId, StringComparison.Ordinal)
            || (
                tokenClientId is not null && !string.Equals(tokenClientId, clientId, StringComparison.Ordinal)
            )
        )
        {
            // Debug, not Warning: an expected answer any authenticated caller can trigger at will.
            logger.LogDebug(
                "Introspection answered inactive: the supplied token was not issued to the calling client {ClientId}",
                LoggingUtility.SanitizeForLog(clientId)
            );
            return new TokenIntrospectionResult.Inactive();
        }

        if (RequiredString(members, "typ") is not { } tokenType)
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "typ is missing or not a string");
        }

        if (!string.Equals(tokenType, AccessTokenType, StringComparison.Ordinal))
        {
            logger.LogDebug(
                "Introspection answered inactive: the supplied token is not an access token; calling client {ClientId}",
                LoggingUtility.SanitizeForLog(clientId)
            );
            return new TokenIntrospectionResult.Inactive();
        }

        if (!TryInteger(members, "exp", out long exp))
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "exp is missing or not an integer");
        }

        if (!TryInteger(members, "iat", out long iat))
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "iat is missing or not an integer");
        }

        if (RequiredString(members, "scope") is not { } scope)
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "scope is missing or not a string");
        }

        if (!TryOptionalString(members, "sub", out string? sub))
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "sub is not a string");
        }

        if (!TryOptionalString(members, "iss", out string? iss))
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "iss is not a string");
        }

        if (!TryAudience(members, out IReadOnlyList<string> aud))
        {
            return AnswerNotUsable(
                UnrecognizedResponseReason,
                clientId,
                "aud is not a string or an array of strings"
            );
        }

        logger.LogDebug(
            "Keycloak answered the introspection request for client {ClientId}: the caller's own access token is active",
            LoggingUtility.SanitizeForLog(clientId)
        );
        return new TokenIntrospectionResult.Active(
            new IntrospectedToken(azp, scope, exp, iat, sub, aud, iss)
        );
    }

    /// <summary>
    /// A non-200 answer, restricted to the characterized ones. 401 is every client-authentication
    /// failure (I-06 to I-09, I-11, I-12): 26.1.4 answers <c>invalid_request</c> "Authentication
    /// failed.", 26.7.5 <c>invalid_client</c>. 403 <c>invalid_request</c> "Client not allowed." is a
    /// public client (I-10). The 400 answers (I-18, I-20, I-21) are requests CMS never sends, so they
    /// are not usable either. Keycloak's status never decides between 400 and 401 for the caller; the
    /// endpoint does that.
    /// </summary>
    private TokenIntrospectionResult MapErrorResponse(
        int statusCode,
        Dictionary<string, JsonElement>? members,
        string clientId
    )
    {
        string? error = members is null ? null : RequiredString(members, "error");
        string? description = members is null ? null : RequiredString(members, "error_description");
        string category = KeycloakProviderAnswer.ProviderErrorCategory(error);

        if (statusCode is not (401 or 403))
        {
            // 400, 404 wrong realm, 3xx, 5xx and every other status: an operator misconfiguration or
            // an outage, still "cannot introspect right now" to the caller.
            return ProviderAnswerNotUsable(ProviderStatusReason, clientId, statusCode, category);
        }

        if (members is null)
        {
            return ProviderAnswerNotUsable(UnparseableReason, clientId, statusCode, category);
        }

        switch (statusCode, error)
        {
            case (401, "invalid_client" or "unauthorized_client" or "invalid_request"):
            case (403, "invalid_request") when description == PublicClientRefusalDescription:
                logger.LogInformation(
                    "Keycloak refused the introspection request for client {ClientId} (HTTP {StatusCode}, {ProviderError})",
                    LoggingUtility.SanitizeForLog(clientId),
                    statusCode,
                    category
                );
                return new TokenIntrospectionResult.InvalidClient();

            default:
                return ProviderAnswerNotUsable(UnrecognizedResponseReason, clientId, statusCode, category);
        }
    }

    /// <summary>
    /// The <paramref name="read"/> members of a JSON object, each cloned so it outlives the parsed
    /// document. Null (unparseable) when the body is missing, oversized, not UTF-8, not JSON or not an
    /// object, when any member name cannot be decoded, when a read member is repeated, or when a string
    /// inside a read member cannot be decoded: a repeated or undecodable member would make the
    /// ownership comparison or the reported value ambiguous. Every name is decoded through
    /// <see cref="JsonProperty.Name"/>, so an escaped name is recognized and an escaped, unpaired
    /// surrogate such as <c>"\uD800"</c>, valid UTF-8 and valid JSON syntax, is rejected wherever it is.
    /// </summary>
    private static Dictionary<string, JsonElement>? ParseMembers(byte[]? body, HashSet<string> read)
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

                if (!StringsDecode(property.Value) || !members.TryAdd(name, property.Value.Clone()))
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
    /// False when the value is a string, or an array holding a string, that cannot be decoded, so the
    /// mapping can read every string it accepts without a fault. Only <c>GetString</c> is guarded.
    /// </summary>
    private static bool StringsDecode(JsonElement value)
    {
        IEnumerable<JsonElement> strings = value.ValueKind switch
        {
            JsonValueKind.String => [value],
            JsonValueKind.Array => value
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

    /// <summary>The member as a string; null when it is absent or not a string.</summary>
    private static string? RequiredString(Dictionary<string, JsonElement> members, string name) =>
        members.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>True with null when the member is absent, true with its value when it is a string, false otherwise.</summary>
    private static bool TryOptionalString(
        Dictionary<string, JsonElement> members,
        string name,
        out string? text
    )
    {
        text = null;
        if (!members.TryGetValue(name, out JsonElement value))
        {
            return true;
        }

        text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text is not null;
    }

    /// <summary>A JSON integer that fits Int64; a fraction, an exponent or a string is not one.</summary>
    private static bool TryInteger(Dictionary<string, JsonElement> members, string name, out long number)
    {
        number = 0;
        return members.TryGetValue(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out number);
    }

    /// <summary>
    /// Keycloak answers a single audience as a string and several as an array (I-01, I-02); absent is no
    /// audience. Any other kind, or an array holding anything but strings, is not recognized.
    /// </summary>
    private static bool TryAudience(
        Dictionary<string, JsonElement> members,
        out IReadOnlyList<string> audience
    )
    {
        audience = [];
        if (!members.TryGetValue("aud", out JsonElement value))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            audience = [value.GetString()!];
            return true;
        }

        if (
            value.ValueKind != JsonValueKind.Array
            || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)
        )
        {
            return false;
        }

        audience = [.. value.EnumerateArray().Select(item => item.GetString()!)];
        return true;
    }

    /// <summary>A 200 answer that is not usable. <paramref name="fixedDetail"/> names a member, never its value.</summary>
    private TokenIntrospectionResult.TemporarilyUnavailable AnswerNotUsable(
        string reason,
        string clientId,
        string fixedDetail
    )
    {
        logger.LogError(
            "Introspection for client {ClientId} could not be completed: Keycloak answered HTTP 200 with a body that is not a recognized introspection answer: {Detail} ({Reason})",
            LoggingUtility.SanitizeForLog(clientId),
            fixedDetail,
            reason
        );
        return new TokenIntrospectionResult.TemporarilyUnavailable(reason);
    }

    private TokenIntrospectionResult.TemporarilyUnavailable ProviderAnswerNotUsable(
        string reason,
        string clientId,
        int statusCode,
        string category
    )
    {
        logger.LogError(
            "Introspection for client {ClientId} could not be completed: Keycloak answered HTTP {StatusCode} ({ProviderError}), which is not a recognized introspection answer ({Reason})",
            LoggingUtility.SanitizeForLog(clientId),
            statusCode,
            category,
            reason
        );
        return new TokenIntrospectionResult.TemporarilyUnavailable(reason);
    }

    /// <summary>
    /// The exception object is deliberately not handed to the logger: any dependency message may carry
    /// anything, so only the chain of type names is recorded (DMS-1327 D-15 rule 1).
    /// </summary>
    private TokenIntrospectionResult.TemporarilyUnavailable IntrospectionFailed(
        string reason,
        string clientId,
        Exception exception
    )
    {
        logger.LogError(
            "Introspection for client {ClientId} could not be completed: the Keycloak introspection request failed ({Reason}, {ExceptionTypes})",
            LoggingUtility.SanitizeForLog(clientId),
            reason,
            KeycloakProviderAnswer.ExceptionTypeChain(exception)
        );
        return new TokenIntrospectionResult.TemporarilyUnavailable(reason);
    }
}
