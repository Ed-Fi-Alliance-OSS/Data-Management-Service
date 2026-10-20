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
                HttpStatusCode.BadRequest when IsInvalidScope(responseString) =>
                    new TokenResult.FailureIdentityProvider(
                        new IdentityProviderError.InvalidScope(responseString)
                    ),
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

    /// <summary>
    /// True when the body is a JSON object whose top-level "error" is the string "invalid_scope".
    /// Any other body shape is not an invalid scope answer.
    /// </summary>
    private static bool IsInvalidScope(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && string.Equals(error.GetString(), "invalid_scope", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
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
