// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.DataModel;
using Flurl.Http;
using Keycloak.Net.Models.Clients;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

/// <summary>
/// RFC 7009 revocation for the Keycloak provider (DMS-1327 D-09, D-10, D-11). Keycloak authenticates
/// the caller inside the delegated revoke request, but it accepts a public client with or without
/// a secret, so a client-type gate runs first: delegation requires affirmative evidence, read through
/// the admin API, that the caller is exactly one confidential, non-bearer-only client. The revoke
/// request then carries only the caller's own credentials, and Keycloak's answer is mapped only
/// through the responses characterized in the design's §9.1; anything else is an operational
/// failure. The token is never decoded here, so ownership is Keycloak's decision alone. Nothing from
/// the caller, a provider body or a dependency exception reaches a log except the sanitized client
/// id, the provider status code, an allowlisted provider error category and exception type names
/// (D-15).
/// </summary>
public sealed class KeycloakTokenRevocationManager(
    KeycloakContext keycloakContext,
    IHttpClientFactory httpClientFactory,
    IKeycloakClientFacade keycloakClientFacade,
    ILogger<KeycloakTokenRevocationManager> logger
) : ITokenRevocationManager
{
    /// <summary>
    /// Keycloak's <c>error_description</c> for a token issued to another client, copied from the
    /// characterization evidence (§9.1, K-02, K-08 and K-20, identical on 26.1.4 and 26.7.5). It is
    /// the only <c>invalid_request</c> answered as completed, and only together with HTTP 400.
    /// </summary>
    internal const string OwnershipMismatchDescription = "Unmatching clients";

    /// <summary>The largest error body read; a larger one is treated as unparseable (D-09).</summary>
    internal const int MaxResponseBodyBytes = 64 * 1024;

    /// <summary>
    /// The named client for the delegated revoke request, registered with automatic redirects off:
    /// a followed redirect could turn an unconfirmed revocation into a 200, and on a 307 or 308 would
    /// resend the form, the caller's secret and token included, to the redirect target. A 3xx is
    /// then answered here like any other unrecognized status (D-09).
    /// </summary>
    public const string HttpClientName = "KeycloakRevocationClient";

    private const string ClientTypeTimeoutReason = "client-type-timeout";
    private const string ClientTypeReadRefusedReason = "client-type-read-refused";
    private const string ClientTypeReadReason = "client-type-read";
    private const string ClientTypeUnknownReason = "client-type-unknown";
    private const string ClientTypeAmbiguousReason = "client-type-ambiguous";
    private const string TimeoutReason = "timeout";
    private const string UnreachableReason = "unreachable";
    private const string ResponseLostReason = "response-lost";
    private const string UnparseableReason = "unparseable";
    private const string UnexpectedReason = "unexpected";
    private const string ProviderStatusReason = "provider-status";
    private const string UnrecognizedResponseReason = "unrecognized-response";

    public async Task<TokenRevocationResult> RevokeTokenAsync(
        TokenRevocationRequest request,
        CancellationToken cancellationToken
    )
    {
        // The endpoint already refuses a request without both credentials (D-11.1); repeated here
        // so that no caller of this manager can reach the provider with a secretless request.
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            return new TokenRevocationResult.InvalidClient();
        }

        // Decided here: a longer id can name no Keycloak client, the admin API's client-type lookup
        // fails on the URL length once the id is long enough, and the caller-sized id is kept out of
        // every log.
        if (KeycloakProviderAnswer.IsLongerThanAnyClientId(request.ClientId))
        {
            logger.LogInformation("Revocation refused: the client id is longer than any Keycloak client id");
            return new TokenRevocationResult.InvalidClient();
        }

        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);

        TokenRevocationResult? refusal = await EnsureConfidentialClientAsync(
            request.ClientId,
            httpClient.Timeout,
            cancellationToken
        );
        if (refusal is not null)
        {
            return refusal;
        }

        return await DelegateRevocationAsync(httpClient, request, cancellationToken);
    }

    /// <summary>
    /// The client-type gate (D-11.2, D-11.3). Returns null only for exactly one client whose id
    /// equals the caller's ordinally, whose <c>publicClient</c> is present and false, and whose
    /// <c>bearerOnly</c> is absent or false. "No such client" is concluded only from a successful
    /// read; a failed, refused or incomplete read is an operational failure, never
    /// <c>invalid_client</c> (Q-07).
    /// </summary>
    private async Task<TokenRevocationResult?> EnsureConfidentialClientAsync(
        string clientId,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        IEnumerable<Client>? clients;
        try
        {
            clients = await keycloakClientFacade.GetClientsByClientIdAsync(
                keycloakContext.Realm,
                clientId,
                timeout,
                cancellationToken
            );
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TimeoutException ex)
        {
            return ClientTypeReadFailed(ClientTypeTimeoutReason, clientId, ex);
        }
        catch (FlurlHttpException ex) when (ex.StatusCode is 401 or 403)
        {
            return ClientTypeReadRefused(clientId, ex);
        }
        catch (Exception ex)
        {
            return ClientTypeReadFailed(ClientTypeReadReason, clientId, ex);
        }

        if (clients is null)
        {
            return ClientTypeNotEstablished(clientId, "the admin API answered no client list");
        }

        List<Client> matches = [];
        foreach (Client? client in clients)
        {
            if (client?.ClientId is null)
            {
                return ClientTypeNotEstablished(clientId, "a client record without a client id was returned");
            }

            // Ordinal: Keycloak client ids are case-sensitive (§9.1 K-03), so a case variant is a
            // different client, which Keycloak itself would refuse.
            if (string.Equals(client.ClientId, clientId, StringComparison.Ordinal))
            {
                matches.Add(client);
            }
        }

        if (matches.Count == 0)
        {
            logger.LogInformation(
                "Revocation refused: no Keycloak client {ClientId} exists in the realm",
                LoggingUtility.SanitizeForLog(clientId)
            );
            return new TokenRevocationResult.InvalidClient();
        }

        if (matches.Count > 1)
        {
            logger.LogError(
                "Revocation for client {ClientId} could not establish the client type: the admin API answered {MatchCount} clients with that id",
                LoggingUtility.SanitizeForLog(clientId),
                matches.Count
            );
            return new TokenRevocationResult.TemporarilyUnavailable(ClientTypeAmbiguousReason);
        }

        Client match = matches[0];

        // Affirmative refusals first: either flag alone proves the caller cannot authenticate
        // as a confidential client, whatever else the record is missing.
        if (match.PublicClient is true || match.BearerOnly is true)
        {
            logger.LogInformation(
                "Revocation refused: Keycloak client {ClientId} is {ClientType}, not a confidential client",
                LoggingUtility.SanitizeForLog(clientId),
                match.PublicClient is true ? "a public client" : "bearer-only"
            );
            return new TokenRevocationResult.InvalidClient();
        }

        if (match.PublicClient is null)
        {
            return ClientTypeNotEstablished(clientId, "the client record does not state publicClient");
        }

        return null;
    }

    /// <summary>
    /// The delegated revoke (D-09). Credentials always travel as form fields
    /// (<c>client_secret_post</c>) so Keycloak sees exactly one mechanism. Headers are awaited
    /// under the client's own timeout; an error body is then read under a fresh budget of the same
    /// length, because <see cref="HttpCompletionOption.ResponseHeadersRead"/> leaves the body
    /// outside that timeout.
    /// </summary>
    private async Task<TokenRevocationResult> DelegateRevocationAsync(
        HttpClient httpClient,
        TokenRevocationRequest request,
        CancellationToken cancellationToken
    )
    {
        List<KeyValuePair<string, string>> form =
        [
            new("client_id", request.ClientId),
            new("client_secret", request.ClientSecret),
            new("token", request.Token),
        ];

        // D-06: only the two RFC 7009 hints are forwarded; Keycloak ignores the value (§9.1
        // K-10…K-13), so this never changes lookup or authorization.
        switch (request.TokenTypeHint)
        {
            case TokenTypeHint.AccessToken:
                form.Add(new("token_type_hint", "access_token"));
                break;
            case TokenTypeHint.RefreshToken:
                form.Add(new("token_type_hint", "refresh_token"));
                break;
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{keycloakContext.Url}/realms/{keycloakContext.Realm}/protocol/openid-connect/revoke"
        );
        message.Content = new FormUrlEncodedContent(form);

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
            // The caller is still waiting, so this is the client's timeout. The request may
            // already have reached Keycloak; the final state is unknown (D-13.3).
            return RevocationFailed(TimeoutReason, request.ClientId, ex);
        }
        catch (HttpRequestException ex)
        {
            return RevocationFailed(
                KeycloakProviderAnswer.IsUndelivered(ex.HttpRequestError)
                    ? UnreachableReason
                    : ResponseLostReason,
                request.ClientId,
                ex
            );
        }
        catch (Exception ex)
        {
            return RevocationFailed(UnexpectedReason, request.ClientId, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                // The status line confirms the outcome; RFC 7009 and Keycloak answer an unknown,
                // invalid or already revoked token with 200 as well (D-10 row 1), so the body is
                // never needed and is not read.
                logger.LogDebug(
                    "Keycloak completed the revocation request for client {ClientId}",
                    LoggingUtility.SanitizeForLog(request.ClientId)
                );
                return new TokenRevocationResult.Completed();
            }

            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                // Decided on the status alone, so the body is never read. Information, not Error: a
                // request any caller can repeat at will, not an outage. No client id: no one was
                // authenticated.
                logger.LogInformation(
                    "Keycloak rejected a revocation request as too large (HTTP 413) before authenticating the caller"
                );
                return new TokenRevocationResult.InvalidRequest();
            }

            byte[]? body;
            try
            {
                using var bodyBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bodyBudget.CancelAfter(httpClient.Timeout);
                body = await ReadBoundedAsync(response.Content, bodyBudget.Token);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex)
                when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                return RevocationFailed(ResponseLostReason, request.ClientId, ex);
            }
            catch (Exception ex)
            {
                return RevocationFailed(UnexpectedReason, request.ClientId, ex);
            }

            return MapErrorResponse((int)response.StatusCode, ParseErrorBody(body), request.ClientId);
        }
    }

    /// <summary>
    /// D-10, restricted to the characterized answers. Keycloak's status never decides between 400
    /// and 401 for the caller; the endpoint does that (D-03). A 413, for a form value over Keycloak's
    /// form attribute limit (128 KiB by default), is decided before the body is read: Keycloak answers
    /// it on 26.1.4 and 26.7.5 before the caller is authenticated, and the caller chose that value.
    /// </summary>
    private TokenRevocationResult MapErrorResponse(int statusCode, ProviderErrorBody body, string clientId)
    {
        string category = KeycloakProviderAnswer.ProviderErrorCategory(body.Error);

        if (statusCode is not (400 or 401))
        {
            // 403 "HTTPS required", 404 wrong realm, 5xx and every other status: an operator
            // misconfiguration or an outage, still "cannot revoke right now" to the caller.
            return ProviderAnswerNotUsable(ProviderStatusReason, clientId, statusCode, category);
        }

        if (!body.Parsed)
        {
            return ProviderAnswerNotUsable(UnparseableReason, clientId, statusCode, category);
        }

        switch (statusCode, body.Error)
        {
            case (400, "invalid_request") when body.Description == OwnershipMismatchDescription:
                logger.LogDebug(
                    "Revocation ignored: Keycloak reports that the supplied token does not belong to the calling client {ClientId}",
                    LoggingUtility.SanitizeForLog(clientId)
                );
                return new TokenRevocationResult.Completed();

            case (400, "unsupported_token_type"):
                LogProviderRefusal(clientId, statusCode, category);
                return new TokenRevocationResult.UnsupportedTokenType();

            case (_, "invalid_client" or "unauthorized_client"):
                LogProviderRefusal(clientId, statusCode, category);
                return new TokenRevocationResult.InvalidClient();

            case (400, "invalid_request"):
                LogProviderRefusal(clientId, statusCode, category);
                return new TokenRevocationResult.InvalidRequest();

            default:
                return ProviderAnswerNotUsable(UnrecognizedResponseReason, clientId, statusCode, category);
        }
    }

    /// <summary>
    /// Reads at most <see cref="MaxResponseBodyBytes"/>; null when the body is larger, so an
    /// oversized answer is unparseable without being buffered.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken
    )
    {
        if (content.Headers.ContentLength > MaxResponseBodyBytes)
        {
            return null;
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[MaxResponseBodyBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total > MaxResponseBodyBytes ? null : buffer[..total];
    }

    /// <summary>
    /// Extracts <c>error</c> and <c>error_description</c> from an RFC 6749 §5.2 object. A body
    /// that is missing, oversized, not UTF-8, not JSON, not an object, has any member name that
    /// cannot be decoded, repeats either member, or holds either member as a string that cannot be
    /// decoded is unparseable: a duplicated or undecodable member would make the exact-match
    /// normalization ambiguous. A member that is not a string, an array included, is treated as
    /// absent, so the strings inside an array are not decoded.
    /// </summary>
    private static ProviderErrorBody ParseErrorBody(byte[]? body)
    {
        if (
            KeycloakProviderAnswer.ParseMembers(
                body,
                KeycloakProviderAnswer.ErrorMembers,
                decodeArrayStrings: false
            )
            is not { } members
        )
        {
            return ProviderErrorBody.Unparseable;
        }

        return new ProviderErrorBody(
            true,
            KeycloakProviderAnswer.StringMember(members, "error"),
            KeycloakProviderAnswer.StringMember(members, "error_description")
        );
    }

    private void LogProviderRefusal(string clientId, int statusCode, string category) =>
        logger.LogInformation(
            "Keycloak refused the revocation request for client {ClientId} (HTTP {StatusCode}, {ProviderError})",
            LoggingUtility.SanitizeForLog(clientId),
            statusCode,
            category
        );

    private TokenRevocationResult.TemporarilyUnavailable ProviderAnswerNotUsable(
        string reason,
        string clientId,
        int statusCode,
        string category
    )
    {
        logger.LogError(
            "Revocation for client {ClientId} could not be confirmed: Keycloak answered HTTP {StatusCode} ({ProviderError}), which is not a recognized revocation answer ({Reason})",
            LoggingUtility.SanitizeForLog(clientId),
            statusCode,
            category,
            reason
        );
        return new TokenRevocationResult.TemporarilyUnavailable(reason);
    }

    private TokenRevocationResult.TemporarilyUnavailable ClientTypeNotEstablished(
        string clientId,
        string fixedDetail
    )
    {
        logger.LogError(
            "Revocation for client {ClientId} could not establish the client type: {Detail}",
            LoggingUtility.SanitizeForLog(clientId),
            fixedDetail
        );
        return new TokenRevocationResult.TemporarilyUnavailable(ClientTypeUnknownReason);
    }

    /// <summary>
    /// The exception object is deliberately not handed to the logger: a Flurl exception's message
    /// carries the request URL, which holds the caller's client id, and any dependency message may
    /// carry anything, so only the chain of type names is recorded (D-15 rule 1).
    /// </summary>
    private TokenRevocationResult.TemporarilyUnavailable ClientTypeReadFailed(
        string reason,
        string clientId,
        Exception exception
    )
    {
        logger.LogError(
            "Revocation for client {ClientId} could not establish the client type: the Keycloak admin read failed (HTTP {StatusCode}, {ExceptionTypes})",
            LoggingUtility.SanitizeForLog(clientId),
            (exception as FlurlHttpException)?.StatusCode,
            KeycloakProviderAnswer.ExceptionTypeChain(exception)
        );
        return new TokenRevocationResult.TemporarilyUnavailable(reason);
    }

    private TokenRevocationResult.TemporarilyUnavailable ClientTypeReadRefused(
        string clientId,
        FlurlHttpException exception
    )
    {
        logger.LogError(
            "Revocation for client {ClientId} could not establish the client type: the Keycloak admin API refused the read (HTTP {StatusCode}). The Configuration Service client lacks permission to read clients in the realm, or its own credentials were rejected ({ExceptionTypes})",
            LoggingUtility.SanitizeForLog(clientId),
            exception.StatusCode,
            KeycloakProviderAnswer.ExceptionTypeChain(exception)
        );
        return new TokenRevocationResult.TemporarilyUnavailable(ClientTypeReadRefusedReason);
    }

    private TokenRevocationResult.TemporarilyUnavailable RevocationFailed(
        string reason,
        string clientId,
        Exception exception
    )
    {
        logger.LogError(
            "Revocation for client {ClientId} could not be confirmed: the Keycloak revoke request failed ({Reason}, {ExceptionTypes})",
            LoggingUtility.SanitizeForLog(clientId),
            reason,
            KeycloakProviderAnswer.ExceptionTypeChain(exception)
        );
        return new TokenRevocationResult.TemporarilyUnavailable(reason);
    }

    private sealed record ProviderErrorBody(bool Parsed, string? Error, string? Description)
    {
        public static readonly ProviderErrorBody Unparseable = new(false, null, null);
    }
}
