// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;

namespace EdFi.DmsConfigurationService.Backend.Keycloak;

public class TokenManager(KeycloakContext keycloakContext) : ITokenManager
{
    public async Task<TokenResult> GetAccessTokenAsync(IEnumerable<KeyValuePair<string, string>> credentials)
    {
        try
        {
            using var client = new HttpClient();

            var contentList = credentials.ToList();
            contentList.AddRange([new KeyValuePair<string, string>("grant_type", "client_credentials")]);

            var content = new FormUrlEncodedContent(contentList);
            string path =
                $"{keycloakContext.Url}/realms/{keycloakContext.Realm}/protocol/openid-connect/token";
            var response = await client.PostAsync(path, content);
            string responseString = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                return new TokenResult.Success(responseString);
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new TokenResult.FailureKeycloak(
                    new KeycloakError.Unauthorized(responseString)
                ),
                HttpStatusCode.Forbidden => new TokenResult.FailureKeycloak(
                    new KeycloakError.Forbidden(responseString)
                ),
                HttpStatusCode.NotFound => new TokenResult.FailureKeycloak(
                    new KeycloakError.NotFound(responseString)
                ),
                _ => new TokenResult.FailureUnknown(responseString),
            };
        }
        catch (HttpRequestException ex)
        {
            return ex.HttpRequestError == HttpRequestError.ConnectionError
                ? new TokenResult.FailureKeycloak(new KeycloakError.Unreachable(ex.Message))
                : new TokenResult.FailureUnknown(ex.Message);
        }
        catch (Exception ex)
        {
            return new TokenResult.FailureUnknown(ex.Message);
        }
    }
}
