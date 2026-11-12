// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DmsConfigurationService.DataModel;
using Microsoft.Extensions.Logging;
using static EdFi.DmsConfigurationService.Backend.Keycloak.KeycloakProviderAnswer;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

/// <summary>
/// RFC 7662 introspection for the Keycloak provider, delegated to the realm's own introspection
/// endpoint with the caller's credentials. Keycloak authenticates the caller, and refuses a public
/// client itself (I-10), but it checks no ownership: a confidential client in the token's
/// <c>aud</c> is told about another client's token (I-04), as any confidential client is before
/// 26.6.2 (I-03). So CMS decides ownership from the answer: only an active access token whose
/// <c>azp</c>, and <c>client_id</c> when present, equal the caller's id ordinally is reported, and only
/// its RFC 7662 members. Keycloak's answer is mapped only through the responses characterized on
/// 26.1.4 and 26.7.5 (<c>KeycloakIntrospectionCharacterizationTests</c>, rows I-00 to I-32); anything
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

    /// <summary>
    /// Keycloak's <c>error_description</c>, with a 400 <c>invalid_request</c>, for any exception after
    /// it authenticated the caller, while it checks the token or builds the answer: a JWT whose header
    /// has no <c>alg</c> (I-32) as much as a failure of its own stores or mappers.
    /// </summary>
    internal const string IntrospectionFailedDescription = "Failed to introspect token.";

    /// <summary>
    /// The named client for the delegated introspection request, registered with automatic redirects
    /// off: a followed redirect could turn an unusable answer into a token outcome, and on a 307 or 308
    /// would resend the form, the caller's secret and token included, to the redirect target. A 3xx is
    /// then answered here like any other unrecognized status.
    /// </summary>
    public const string HttpClientName = "KeycloakIntrospectionClient";

    /// <summary>
    /// The <c>typ</c> values of a Keycloak access token: <c>Bearer</c>, and <c>DPoP</c> for one bound to
    /// a DPoP key (Keycloak 26.7.5 <c>DPoPUtil</c>), which Keycloak introspects as active for its
    /// confidential client. A refresh token is <c>Refresh</c> (I-16) and an ID token <c>ID</c>.
    /// Matched without regard to case, since a client with
    /// <c>token.response.type.bearer.lower-case</c> gets access tokens typed <c>bearer</c>; the
    /// canonical spelling stored here is the one reported.
    /// </summary>
    private static readonly string[] _accessTokenTypes = ["Bearer", "DPoP"];

    private const string TimeoutReason = "timeout";
    private const string UnreachableReason = "unreachable";
    private const string ResponseLostReason = "response-lost";
    private const string UnparseableReason = "unparseable";
    private const string UnexpectedReason = "unexpected";
    private const string ProviderStatusReason = "provider-status";
    private const string UnrecognizedResponseReason = "unrecognized-response";
    private const string UndecidedTokenReason = "undecided-token";

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

        // Decided here: a longer id can name no Keycloak client, so asking Keycloak could not change
        // the answer, and the caller-sized id is kept out of every log.
        if (KeycloakProviderAnswer.IsLongerThanAnyClientId(request.ClientId))
        {
            logger.LogInformation(
                "Introspection refused: the client id is longer than any Keycloak client id"
            );
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

        // Keycloak 26.1.4 answers a form value over its form attribute limit with 413 without reading
        // the rest of the request and keeps the connection open, so the next request on that
        // connection gets no answer; closing the connection after each answer keeps a caller's
        // oversized request from stalling another caller's.
        message.Headers.ConnectionClose = true;

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
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                // Decided on the status alone, so the body is never read. Information, not Error: a
                // request any caller can repeat at will, not an outage. No client id: no one was
                // authenticated.
                logger.LogInformation(
                    "Keycloak rejected an introspection request as too large (HTTP 413) before authenticating the caller"
                );
                return new TokenIntrospectionResult.InvalidRequest();
            }

            // Headers were awaited under the client's own timeout; the body is read under a fresh
            // budget of the same length, because ResponseHeadersRead leaves it outside that timeout.
            // The body is read whole: Keycloak rebuilds an answer's claims from the client's mappers
            // as they are when it answers (I-31), so the answer's size follows what administrators
            // configure for the client, not the token's length, and no fixed cap holds every answer.
            byte[] body;
            try
            {
                using var bodyBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bodyBudget.CancelAfter(httpClient.Timeout);
                body = await response.Content.ReadAsByteArrayAsync(bodyBudget.Token);
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

            // Strings inside arrays are decoded too, because aud is read as an array of strings.
            return response.StatusCode == HttpStatusCode.OK
                ? MapAnswer(ParseMembers(body, _answerMembers, decodeArrayStrings: true), request.ClientId)
                : MapErrorResponse(
                    (int)response.StatusCode,
                    ParseMembers(body, ErrorMembers, decodeArrayStrings: true),
                    request.ClientId
                );
        }
    }

    /// <summary>
    /// A 200 answer (I-01 to I-05, I-13 to I-17, I-19, I-22 to I-24, I-27). <c>active:false</c> is how
    /// Keycloak answered every unknown, invalid, expired and revoked token characterized (I-13 to I-15,
    /// I-19). An active answer is a token outcome only for
    /// the caller's own access token; ownership is decided before the token type, so another client's
    /// token is inactive whatever its type. Members are required to have exactly the kinds Keycloak was
    /// observed to send.
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

        if (StringMember(members, "azp") is not { } azp)
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "azp is missing or not a string");
        }

        if (!IsAbsentOrString(members, "client_id"))
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "client_id is not a string");
        }

        // Ordinal: Keycloak client ids are case-sensitive, so a case variant is another client.
        if (
            !string.Equals(azp, clientId, StringComparison.Ordinal)
            || (
                StringMember(members, "client_id") is { } tokenClientId
                && !string.Equals(tokenClientId, clientId, StringComparison.Ordinal)
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

        if (StringMember(members, "typ") is not { } tokenType)
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "typ is missing or not a string");
        }

        if (
            Array.Find(
                _accessTokenTypes,
                type => string.Equals(type, tokenType, StringComparison.OrdinalIgnoreCase)
            )
            is not { } accessTokenType
        )
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

        // Keycloak leaves scope out when the token has none (I-27), as RFC 7662 §2.2 allows.
        if (!IsAbsentOrString(members, "scope"))
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "scope is not a string");
        }

        if (!IsAbsentOrString(members, "sub"))
        {
            return AnswerNotUsable(UnrecognizedResponseReason, clientId, "sub is not a string");
        }

        if (!IsAbsentOrString(members, "iss"))
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
        // No scope is an empty one, as the self-contained provider reports a token without scopes.
        return new TokenIntrospectionResult.Active(
            new IntrospectedToken(
                azp,
                StringMember(members, "scope") ?? string.Empty,
                exp,
                iat,
                StringMember(members, "sub"),
                aud,
                StringMember(members, "iss"),
                accessTokenType
            )
        );
    }

    /// <summary>
    /// A non-200 answer other than 413, restricted to the characterized ones. 401 is a failed client
    /// authentication (I-06 to I-09, I-12): 26.1.4 answers <c>invalid_request</c> "Authentication
    /// failed.", 26.7.5 <c>invalid_client</c>. 26.7.5 also answers 401 <c>invalid_request</c>
    /// "Authentication failed." when authenticating the caller throws, which cannot be told apart from
    /// 26.1.4's answer to wrong credentials, so both are <see cref="TokenIntrospectionResult.InvalidClient"/>.
    /// A bearer-only client with its secret is refused on 26.1.4 (I-11, 401) and authenticated on
    /// 26.7.5 (I-11, 200); a bearer-only client obtains no tokens, so it is never told about one.
    /// 403 <c>invalid_request</c> "Client not allowed." is a public client (I-10). 400
    /// <c>invalid_request</c> <see cref="IntrospectionFailedDescription"/> comes after the caller is
    /// authenticated, but does not tell a malformed token from a failure inside Keycloak (I-32), so it
    /// is <see cref="TokenIntrospectionResult.TemporarilyUnavailable"/>: answering it inactive could
    /// tell an owner its live token is not active. It is logged at Warning, not Error, because any
    /// caller can cause it with a malformed token. The other 400 answers (I-18, I-20, I-21) are
    /// requests CMS never sends, so they are not usable. Keycloak's status never decides between 400
    /// and 401 for the caller; the endpoint does that.
    /// </summary>
    private TokenIntrospectionResult MapErrorResponse(
        int statusCode,
        Dictionary<string, JsonElement>? members,
        string clientId
    )
    {
        if (
            statusCode == (int)HttpStatusCode.BadRequest
            && members is not null
            && StringMember(members, "error") == "invalid_request"
            && StringMember(members, "error_description") == IntrospectionFailedDescription
        )
        {
            logger.LogWarning(
                "Introspection for client {ClientId} could not be completed: Keycloak failed while checking the token (HTTP 400), which a malformed token or a Keycloak failure causes alike ({Reason})",
                LoggingUtility.SanitizeForLog(clientId),
                UndecidedTokenReason
            );
            return new TokenIntrospectionResult.TemporarilyUnavailable(UndecidedTokenReason);
        }

        string category = KeycloakProviderAnswer.ProviderErrorCategory(
            members is null ? null : StringMember(members, "error")
        );

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

        switch (statusCode, StringMember(members, "error"), StringMember(members, "error_description"))
        {
            case (401, "invalid_client" or "unauthorized_client" or "invalid_request", _):
            case (403, "invalid_request", PublicClientRefusalDescription):
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

    /// <summary>False when the member is present but not a string, <c>null</c> included: null is not absent.</summary>
    private static bool IsAbsentOrString(Dictionary<string, JsonElement> members, string name) =>
        !members.TryGetValue(name, out JsonElement value) || value.ValueKind == JsonValueKind.String;

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
