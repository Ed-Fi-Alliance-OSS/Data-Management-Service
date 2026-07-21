// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

public class KeycloakTokenManager(
    KeycloakContext keycloakContext,
    ILogger<KeycloakTokenManager> logger,
    IHttpClientFactory httpClientFactory
) : ITokenManager
{
    public async Task<TokenResult> GetAccessTokenAsync(IEnumerable<KeyValuePair<string, string>> credentials)
    {
        try
        {
            var client = httpClientFactory.CreateClient("KeycloakClient");

            var contentList = credentials.ToList();

            var content = new FormUrlEncodedContent(contentList);
            string path =
                $"{keycloakContext.Url}/realms/{keycloakContext.Realm}/protocol/openid-connect/token";
            using var response = await client.PostAsync(path, content);
            string responseString = await response.Content.ReadAsStringAsync();

            return response.StatusCode switch
            {
                HttpStatusCode.OK => new TokenResult.Success(responseString),
                HttpStatusCode.Unauthorized => new TokenResult.FailureIdentityProvider(
                    new IdentityProviderError.Unauthorized(responseString)
                ),
                HttpStatusCode.Forbidden => new TokenResult.FailureIdentityProvider(
                    new IdentityProviderError.Forbidden(responseString)
                ),
                HttpStatusCode.NotFound => new TokenResult.FailureIdentityProvider(
                    new IdentityProviderError.NotFound(responseString)
                ),
                // OAuth 2.0 client errors (RFC 6749 section 5.2) arrive as HTTP 400 with a machine-readable
                // "error" code (invalid_scope, invalid_grant, ...). Preserve the code so the caller learns
                // its request was rejected rather than seeing it collapsed into a retryable server outage.
                HttpStatusCode.BadRequest => MapBadRequest(responseString),
                _ => new TokenResult.FailureIdentityProvider(new IdentityProviderError(responseString)),
            };
        }
        catch (HttpRequestException ex)
        {
            logger.LogCritical(ex, "Get access token error");
            return ex.HttpRequestError == HttpRequestError.ConnectionError
                ? new TokenResult.FailureIdentityProvider(new IdentityProviderError.Unreachable(ex.Message))
                : new TokenResult.FailureUnknown(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Get access token error");
            return new TokenResult.FailureUnknown(ex.Message);
        }
    }

    // Maps an HTTP 400 token response to the appropriate failure. Keycloak reports a client-authentication
    // failure as HTTP 400 with error=invalid_client (rather than 401), so that case is preserved as an
    // InvalidClient failure — letting the token endpoint answer 401 with the Basic WWW-Authenticate
    // challenge and a generic description, following the established client-authentication policy. Every
    // other 400 stays a BadRequest client error carrying its parsed OAuth error code.
    private static TokenResult MapBadRequest(string responseString)
    {
        string errorCode = ParseOAuthErrorCode(responseString);
        return string.Equals(errorCode, "invalid_client", StringComparison.Ordinal)
            ? new TokenResult.FailureIdentityProvider(new IdentityProviderError.InvalidClient(responseString))
            : new TokenResult.FailureIdentityProvider(
                new IdentityProviderError.BadRequest(errorCode, responseString)
            );
    }

    // RFC 6749 section 5.2 error responses carry a machine-readable "error" code in the JSON body.
    // Extract it so the token endpoint can return the corresponding OAuth error to the caller instead of
    // misclassifying a client mistake as a server outage; fall back to invalid_request when the provider
    // response is not the expected shape.
    private static string ParseOAuthErrorCode(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
            )
            {
                string? code = error.GetString();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    return code;
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON or malformed body; fall through to the generic client-error code.
        }

        return "invalid_request";
    }

    async Task<IEnumerable<(RSAParameters RsaParameters, string KeyId)>> ITokenManager.GetPublicKeysAsync()
    {
        await Task.CompletedTask;
        throw new NotImplementedException("GetPublicKeysAsync not yet implemented for Keycloak");
    }

    async Task<bool> ITokenManager.ValidateTokenAsync(string rawToken)
    {
        await Task.CompletedTask;
        throw new NotImplementedException();
    }
}
